// Zarr provider tests. Fully hermetic: metadata/parse/codegen tests are
// pure, and the live tests GENERATE their fixture stores on the fly via
// ZarrProvider.ZarrWrite (pure .NET file writes — no external library, no
// committed binary fixture; contrast NetcdfTests' sample.nc + libnetcdf).
// Only the e2e compile+run blocks need g++, and they skip gracefully
// without it (Build.isSkipError), mirroring NetcdfTests' discipline.
module Blade.Tests.ZarrTests

open System
open System.IO
open Blade
open Blade.Ast
open Blade.Parser
open Blade.IR
open Blade.Types
open Blade.TypeEnv
open Blade.Lowering
open Blade.CodeGen
open Blade.ZarrProvider
open Blade.Build
open Blade.Tests.TestHarness

let runZarrTests () =
    printHeader "Zarr Provider Tests"
    let mutable passed = 0
    let mutable failed = 0

    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s — %s" name detail
            failed <- failed + 1

    let isError (r: Result<'a, string>) (needle: string) =
        match r with
        | Error e -> e.Contains needle
        | Ok _ -> false

    // ---------------------------------------------------------------
    // 1. Dtype mapping (pure)
    // ---------------------------------------------------------------
    printfn "\n--- dtype mapping ---"
    check "v2 <f8 -> ETFloat64/8"
        (match zarrDtypeV2 "<f8" with Ok d -> d.Elem = ETFloat64 && d.ByteSize = 8 && d.IsFloat | _ -> false) ""
    check "v2 <f4 -> ETFloat32/4"
        (match zarrDtypeV2 "<f4" with Ok d -> d.Elem = ETFloat32 && d.ByteSize = 4 | _ -> false) ""
    check "v2 <i4 -> ETInt64/4 (integer collapse)"
        (match zarrDtypeV2 "<i4" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 4 && not d.IsFloat | _ -> false) ""
    check "v2 |i1 -> ETInt64/1"
        (match zarrDtypeV2 "|i1" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 1 | _ -> false) ""
    check "v2 <u2 -> ETInt64/2"
        (match zarrDtypeV2 "<u2" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 2 | _ -> false) ""
    check "v2 >f8 rejected (big-endian)"
        (isError (zarrDtypeV2 ">f8") "big-endian") ""
    check "v2 |b1 rejected (bool)"
        (isError (zarrDtypeV2 "|b1") "unsupported dtype") ""
    check "v3 float32 -> ETFloat32"
        (match zarrDtypeV3 "float32" with Ok d -> d.Elem = ETFloat32 | _ -> false) ""
    check "v3 uint64 -> ETInt64"
        (match zarrDtypeV3 "uint64" with Ok d -> d.Elem = ETInt64 && d.ByteSize = 8 | _ -> false) ""
    check "v3 bool rejected"
        (isError (zarrDtypeV3 "bool") "unsupported data_type") ""

    // ---------------------------------------------------------------
    // 2. v2 .zarray parsing (pure JSON)
    // ---------------------------------------------------------------
    printfn "\n--- v2 metadata parse ---"
    let v2good = """{"zarr_format": 2, "shape": [5, 7], "chunks": [2, 3], "dtype": "<f8",
                     "compressor": null, "fill_value": -1.5, "order": "C", "filters": null}"""
    (match parseArrayMetaV2 "A" "/tmp/s/A" v2good (Some """{"_ARRAY_DIMENSIONS": ["x", "y"]}""") with
     | Ok m ->
         check "v2 parse: shape/chunks" (m.Shape = [5L; 7L] && m.Chunks = [2L; 3L]) (sprintf "%A" m)
         check "v2 parse: dtype f8" (m.Dtype.Code = "f8") m.Dtype.Code
         check "v2 parse: fill -1.5" (m.FillValue = FillFloat -1.5) (sprintf "%A" m.FillValue)
         check "v2 parse: dim names from _ARRAY_DIMENSIONS" (m.DimNames = Some ["x"; "y"]) (sprintf "%A" m.DimNames)
         check "v2 parse: '.' separator, no prefix" (m.ChunkKeySep = "." && m.ChunkKeyPrefix = "") ""
     | Error e -> check "v2 parse: good array parses" false e)
    check "v2 parse: blosc compressor rejected BY NAME"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":{"id":"blosc","cname":"lz4"},"fill_value":0,"order":"C","filters":null}""" None) "blosc") ""
    check "v2 parse: order F rejected"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"F","filters":null}""" None) "order 'F'") ""
    check "v2 parse: filters rejected"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":[{"id":"delta"}]}""" None) "filters") ""
    check "v2 parse: null fill -> FillNone"
        (match parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<i8","compressor":null,"fill_value":null,"order":"C","filters":null}""" None with
         | Ok m -> m.FillValue = FillNone | _ -> false) ""
    check "v2 parse: \"NaN\" fill"
        (match parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":"NaN","order":"C","filters":null}""" None with
         | Ok m -> (match m.FillValue with FillFloat f -> Double.IsNaN f | _ -> false) | _ -> false) ""
    check "v2 parse: '/' dimension_separator honored"
        (match parseArrayMetaV2 "A" "d" """{"shape":[4],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null,"dimension_separator":"/"}""" None with
         | Ok m -> m.ChunkKeySep = "/" | _ -> false) ""
    check "v2 parse: rank mismatch rejected"
        (isError (parseArrayMetaV2 "A" "d" """{"shape":[4,5],"chunks":[2],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" None) "rank") ""

    // ---------------------------------------------------------------
    // 3. v3 zarr.json parsing (pure JSON)
    // ---------------------------------------------------------------
    printfn "\n--- v3 metadata parse ---"
    let v3good = """{"zarr_format": 3, "node_type": "array", "shape": [6, 4], "data_type": "float32",
                     "chunk_grid": {"name": "regular", "configuration": {"chunk_shape": [3, 4]}},
                     "chunk_key_encoding": {"name": "default", "configuration": {"separator": "/"}},
                     "fill_value": 0, "codecs": [{"name": "bytes", "configuration": {"endian": "little"}}],
                     "dimension_names": ["t", "p"]}"""
    (match parseArrayMetaV3 "B" "/tmp/s/B" v3good with
     | Ok m ->
         check "v3 parse: shape/chunks" (m.Shape = [6L; 4L] && m.Chunks = [3L; 4L]) (sprintf "%A" m)
         check "v3 parse: dtype f4" (m.Dtype.Code = "f4") m.Dtype.Code
         check "v3 parse: dimension_names" (m.DimNames = Some ["t"; "p"]) (sprintf "%A" m.DimNames)
         check "v3 parse: 'c' prefix + '/' separator" (m.ChunkKeyPrefix = "c" && m.ChunkKeySep = "/") ""
     | Error e -> check "v3 parse: good array parses" false e)
    check "v3 parse: gzip codec rejected BY NAME"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[2]}},"fill_value":0,"codecs":[{"name":"bytes"},{"name":"gzip","configuration":{"level":5}}]}""") "gzip") ""
    check "v3 parse: big-endian bytes codec rejected"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[2]}},"fill_value":0,"codecs":[{"name":"bytes","configuration":{"endian":"big"}}]}""") "big-endian") ""
    check "v3 parse: v2 chunk_key_encoding (no prefix, '.' default)"
        (match parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[2]}},"fill_value":0,"codecs":[{"name":"bytes"}],"chunk_key_encoding":{"name":"v2"}}""" with
         | Ok m -> m.ChunkKeyPrefix = "" && m.ChunkKeySep = "." | _ -> false) ""
    check "v3 parse: missing chunk_shape rejected"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"regular"},"fill_value":0}""") "chunk_shape") ""
    check "v3 parse: non-regular chunk_grid rejected"
        (isError (parseArrayMetaV3 "B" "d" """{"zarr_format":3,"node_type":"array","shape":[4],"data_type":"float64","chunk_grid":{"name":"rectilinear"},"fill_value":0}""") "regular") ""

    // ---------------------------------------------------------------
    // 4. Chunk keys + grid math (pure)
    // ---------------------------------------------------------------
    printfn "\n--- chunk keys + grid math ---"
    let mkMeta prefix sep = {
        Name = "A"; ArrayDir = "d"; Shape = [5L; 7L]; Chunks = [2L; 3L]
        Dtype = { Code = "f8"; Elem = ETFloat64; ByteSize = 8; IsFloat = true }
        DimNames = None; FillValue = FillNone; Codec = CodecIdentity; Blade = None
        Version = 2; ChunkKeySep = sep; ChunkKeyPrefix = prefix }
    check "key v2 [0;1] -> 0.1" (chunkKey (mkMeta "" ".") [0L; 1L] = "0.1") (chunkKey (mkMeta "" ".") [0L; 1L])
    check "key v2 rank-0 -> 0" (chunkKey (mkMeta "" ".") [] = "0") ""
    check "key v3 [2;3] -> c/2/3" (chunkKey (mkMeta "c" "/") [2L; 3L] = "c/2/3") (chunkKey (mkMeta "c" "/") [2L; 3L])
    check "key v3 rank-0 -> c" (chunkKey (mkMeta "c" "/") [] = "c") ""
    check "key v2 '/'-separated -> 0/1" (chunkKey (mkMeta "" "/") [0L; 1L] = "0/1") ""
    check "gridDims [5;7]/[2;3] = [3;3]" (gridDims [5L; 7L] [2L; 3L] = [3L; 3L]) (sprintf "%A" (gridDims [5L; 7L] [2L; 3L]))
    check "gridCoords count = 9" ((gridCoords [5L; 7L] [2L; 3L]).Length = 9) ""
    check "rowMajorStrides [5;7] = [7;1]" (rowMajorStrides [5; 7] = [7; 1]) (sprintf "%A" (rowMajorStrides [5; 7]))
    check "rowMajorStrides [2;3;4] = [12;4;1]" (rowMajorStrides [2; 3; 4] = [12; 4; 1]) ""

    // ---------------------------------------------------------------
    // 5. Module construction from a mock store (pure; mirrors NetcdfTests 2-3b)
    // ---------------------------------------------------------------
    printfn "\n--- zarrStoreToModule (mock store) ---"
    let mockDt code elem bs isF = { Code = code; Elem = elem; ByteSize = bs; IsFloat = isF }
    let mockStore = {
        Path = "/mock/store"; Version = 2
        Arrays = [
            { Name = "A"; ArrayDir = "/mock/store/A"; Shape = [4L; 3L]; Chunks = [4L; 3L]
              Dtype = mockDt "f4" ETFloat32 4 true; DimNames = Some ["x"; "y"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
            { Name = "x"; ArrayDir = "/mock/store/x"; Shape = [4L]; Chunks = [4L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = Some ["x"]
              FillValue = FillInt 0L; Codec = CodecIdentity; Blade = None; Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
        ]
    }
    let builder = IRBuilder()
    let modul = zarrStoreToModule builder "sample" mockStore None
    let indexDefs = modul.Types |> List.choose (function IRTDIndexType (n, it) -> Some (n, it) | _ -> None)
    check "mock module: named index types x, y"
        (indexDefs |> List.map fst |> List.sort = ["x"; "y"]) (sprintf "%A" (List.map fst indexDefs))
    let dimsFields =
        modul.Types |> List.tryPick (function IRTDStruct ("dims", fs) -> Some fs | _ -> None) |> Option.defaultValue []
    let varsFields =
        modul.Types |> List.tryPick (function IRTDStruct ("vars", fs) -> Some fs | _ -> None) |> Option.defaultValue []
    check "mock module: dims has x and y" (dimsFields |> List.map fst |> List.sort = ["x"; "y"]) (sprintf "%A" (List.map fst dimsFields))
    check "mock module: vars has A only (x is a coordinate array)"
        (varsFields |> List.map fst = ["A"]) (sprintf "%A" (List.map fst varsFields))
    let elemOfArrow (t: IRType) =
        match t with
        | ArrayElem at -> Some at.ElemType
        | _ -> None
    check "mock module: coordinate x keeps its ACTUAL f8 elem (Zarr divergence from NetCDF's Int64)"
        (dimsFields |> List.tryFind (fun (n, _) -> n = "x")
         |> Option.bind (snd >> elemOfArrow) = Some (IRTScalar ETFloat64))
        (sprintf "%A" (dimsFields |> List.tryFind (fun (n, _) -> n = "x") |> Option.bind (snd >> elemOfArrow)))
    check "mock module: unnamed-dim y coordinate defaults to Int64"
        (dimsFields |> List.tryFind (fun (n, _) -> n = "y")
         |> Option.bind (snd >> elemOfArrow) = Some (IRTScalar ETInt64))
        ""
    (let aIdxIds =
        varsFields |> List.tryPick (fun (n, t) ->
            if n <> "A" then None else
            match t with
            | ArrayElem at -> Some (at.IndexTypes |> List.map (fun ix -> ix.Id))
            | _ -> None)
     let xId = indexDefs |> List.tryPick (fun (n, it) -> if n = "x" then Some it.Id else None)
     check "mock module: A's first index IS the shared x index type (same Id)"
         (match aIdxIds, xId with
          | Some (a0 :: _), Some x -> a0 = x
          | _ -> false)
         (sprintf "A ids %A, x id %A" aIdxIds xId))
    check "mock module: conflicting dim extents rejected"
        (try
            let bad = { mockStore with
                          Arrays = mockStore.Arrays @ [ { mockStore.Arrays.[0] with Name = "C"; Shape = [9L; 3L] } ] }
            zarrStoreToModule (IRBuilder()) "s" bad None |> ignore
            false
         with ex -> ex.Message.Contains "conflicting extents") ""

    // ---------------------------------------------------------------
    // 6. Live store roundtrips (generated fixtures; F#-only, no g++)
    // ---------------------------------------------------------------
    printfn "\n--- live store write -> load -> read (v2 and v3) ---"
    let scratch = Path.Combine(Path.GetTempPath(), "blade_zarr_tests_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory scratch |> ignore
    let aData = [| for i in 0 .. 34 -> float i * 1.5 |]
    let xCoord = [| 1.0; 2.0; 3.0; 4.0; 5.0 |]
    let bData = [| for i in 0 .. 11 -> int32 (100 + i) |]
    let mkVars () : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [5L; 7L]; Chunks = [2L; 3L]
          FillValue = FillFloat -1.0; Data = ZarrWrite.WF64 aData; OmitChunks = []; Blade = None }
        { Name = "x"; DimNames = Some ["x"]; Shape = [5L]; Chunks = [5L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 xCoord; OmitChunks = []; Blade = None }
        // B: 3x4 int32, chunks 2x2, chunk (1,1) omitted -> fill 99 there.
        { Name = "B"; DimNames = Some ["r"; "c"]; Shape = [3L; 4L]; Chunks = [2L; 2L]
          FillValue = FillInt 99L; Data = ZarrWrite.WI32 bData; OmitChunks = [[1L; 1L]]; Blade = None }
    ]
    let expectB () =
        // Row-major 3x4; chunk (1,1) covers rows 2, cols 2..3 -> fill 99.
        [| for r in 0 .. 2 do
             for c in 0 .. 3 ->
               if r >= 2 && c >= 2 then 99L else int64 (100 + r * 4 + c) |]
    for (version, writer) in [ (2, ZarrWrite.writeStoreV2); (3, ZarrWrite.writeStoreV3) ] do
        let root = Path.Combine(scratch, sprintf "store_v%d" version)
        writer root (mkVars ())
        (try
            let store = load root
            check (sprintf "v%d: load discovers 3 arrays" version)
                (store.Version = version && (store.Arrays |> List.map (fun a -> a.Name) |> List.sort) = ["A"; "B"; "x"])
                (sprintf "version %d arrays %A" store.Version (store.Arrays |> List.map (fun a -> a.Name)))
            (match tryFindArray store "A" with
             | Some m ->
                 check (sprintf "v%d: A meta roundtrip (shape/chunks/dims/fill)" version)
                     (m.Shape = [5L; 7L] && m.Chunks = [2L; 3L] && m.DimNames = Some ["x"; "y"] && m.FillValue = FillFloat -1.0)
                     (sprintf "%A" m)
             | None -> check (sprintf "v%d: A found" version) false "")
            (match readVarData root "A" with
             | Ok { DimLengths = [5; 7]; Payload = ZFloats got } ->
                 check (sprintf "v%d: A values roundtrip through multi-chunk assembly (edge chunks)" version)
                     (got = aData) (sprintf "first few: %A vs %A" (Array.truncate 5 got) (Array.truncate 5 aData))
             | Ok d -> check (sprintf "v%d: A values roundtrip" version) false (sprintf "unexpected payload %A" d.DimLengths)
             | Error e -> check (sprintf "v%d: A values roundtrip" version) false e)
            (match readVarData root "B" with
             | Ok { Payload = ZInts got } ->
                 check (sprintf "v%d: B omitted chunk reads as fill (99), int32 widened to int64" version)
                     (got = expectB ()) (sprintf "got %A" got)
             | Ok _ -> check (sprintf "v%d: B fill/widening" version) false "not ints"
             | Error e -> check (sprintf "v%d: B fill/widening" version) false e)
         with ex -> check (sprintf "v%d: store loads" version) false ex.Message)
    // Missing chunk + null fill = loud error, not silent zeros.
    (let root = Path.Combine(scratch, "store_nullfill")
     ZarrWrite.writeStoreV2 root [
        { Name = "N"; DimNames = None; Shape = [4L]; Chunks = [2L]
          FillValue = FillNone; Data = ZarrWrite.WF64 [| 1.0; 2.0; 3.0; 4.0 |]; OmitChunks = [[1L]]; Blade = None } ]
     check "null fill + missing chunk -> loud refusal"
         (isError (readVarData root "N") "refusing to invent data") (sprintf "%A" (readVarData root "N")))

    // ---------------------------------------------------------------
    // 7. C++ generator string checks
    // ---------------------------------------------------------------
    printfn "\n--- CppZarr generators ---"
    let f64Idx (builder: IRBuilder) n =
        { Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt n); Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    let wArrType =
        let b = IRBuilder()
        { ElemType = IRTScalar ETFloat64
          IndexTypes = [f64Idx b 5L; f64Idx b 7L]
          IsVirtual = false; Identity = Some (AIDVariable "A") }
    let wCode = CppZarr.genWriteVar "out_store" "A" "A" wArrType ["x"; "y"] |> String.concat "\n"
    check "genWriteVar: creates the array directory" (wCode.Contains "create_directories" && wCode.Contains "out_store/A") wCode
    check "genWriteVar: writes v2 .zarray with compressor null"
        (wCode.Contains ".zarray" && wCode.Contains "zarr_format" && wCode.Contains "compressor") ""
    check "genWriteVar: records _ARRAY_DIMENSIONS" (wCode.Contains "_ARRAY_DIMENSIONS" && wCode.Contains "\\\"x\\\"") ""
    check "genWriteVar: single whole-array chunk 0.0" (wCode.Contains "/0.0") ""
    check "genWriteVar: binary chunk write from the flat buffer"
        (wCode.Contains "A_flat" && wCode.Contains "sizeof(double) * 35") ""
    check "genWriteVar: loud on stream failure" (wCode.Contains "Zarr error" && wCode.Contains "std::exit(1)") ""
    // genReadVar needs real store metadata: reuse the v2 fixture from section 6.
    (let root = Path.Combine(scratch, "store_v2")
     let rCode = CppZarr.genReadVar root "A" "A" wArrType |> String.concat "\n"
     check "genReadVar: metadata existence check, loud" (rCode.Contains ".zarray" && rCode.Contains "Zarr error") ""
     check "genReadVar: fstream chunk reads with computed keys"
         (rCode.Contains "std::ifstream" && rCode.Contains "std::to_string(A_c0)") ""
     check "genReadVar: fill_value branch for missing chunks" (rCode.Contains "A_fillv") ""
     check "genReadVar: short-read guard" (rCode.Contains "gcount()") ""
     check "genReadVar: materializes nested Array (allocate + promote)"
         (rCode.Contains "allocate<typename promote<double, 2>::type") ""
     check "genReadVar: releases buffers" (rCode.Contains "delete[] A_flat" && rCode.Contains "delete[] A_cbuf") "")

    // ---------------------------------------------------------------
    // 8. Registry + surface gates
    // ---------------------------------------------------------------
    printfn "\n--- registry + module-surface gates ---"
    Blade.ProviderStatics.install ()
    check "registry: zarr registered"
        (match Blade.ProviderRegistry.tryFind "zarr" with Some s -> s.Name = "zarr" | None -> false) ""
    check "registry: netcdf registered too"
        ((Blade.ProviderRegistry.names ()) |> List.sort = ["netcdf"; "zarr"]) (sprintf "%A" (Blade.ProviderRegistry.names ()))
    check "registry: zarr rejects load_compound (no compound reader)"
        (match Blade.ProviderRegistry.tryFind "zarr" with Some s -> s.GenReadCompoundVar.IsNone | None -> false) ""
    check "registry: zarr needs no link flags"
        (match Blade.ProviderRegistry.tryFind "zarr" with Some s -> s.LinkNeeds.Contains "none" | None -> false) ""
    let typeErrOf (src: string) : string =
        match Parser.parseProgram src with
        | Error e -> e.Message
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error errs -> errs |> List.map TypeEnv.formatCompileError |> String.concat "; "
            | Ok _ -> ""
    check "old spelling `import Providers.NetCDF` -> steering error"
        ((typeErrOf "import Providers.NetCDF as NetCDF\nlet x = 1\n").Contains "import netcdf as")
        (typeErrOf "import Providers.NetCDF as NetCDF\nlet x = 1\n")
    check "`from zarr import load` -> selective import rejected"
        ((typeErrOf "from zarr import load\nlet x = 1\n").Contains "selective import")
        (typeErrOf "from zarr import load\nlet x = 1\n")
    check "bare `|> read` is a hard break (unbound identifier)"
        ((typeErrOf "let x = 5 |> read\n").Contains "read")
        (typeErrOf "let x = 5 |> read\n")

    // ---------------------------------------------------------------
    // 9. Static fold e2e (hermetic — needs no g++, no external library)
    // ---------------------------------------------------------------
    printfn "\n--- provider statics: zarr fold + ceiling ---"
    (let foldRoot = "zarr_fold_store"
     (try Directory.Delete(foldRoot, true) with _ -> ())
     ZarrWrite.writeStoreV2 foldRoot [
        { Name = "x"; DimNames = Some ["x"]; Shape = [6L]; Chunks = [3L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0 |]; OmitChunks = []; Blade = None }
        { Name = "A"; DimNames = Some ["x"]; Shape = [6L]; Chunks = [6L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 1 .. 6 -> float (i * i) |]; OmitChunks = []; Blade = None } ]
     let foldSource = """
import zarr as z

let sample = z.load("zarr_fold_store")
let static xd = sample.dims.x |> z.read
let static n = length(xd)
let static ps = prodsum(xd, xd)
let a = n
let b = ps
"""
     (try
         match Parser.parseProgram foldSource with
         | Error e -> check "zarr fold: parses" false e.Message
         | Ok program ->
             match TypeCheck.typeCheck program with
             | Error errs ->
                 check "zarr fold: typechecks (fold succeeded)" false
                     (errs |> List.map TypeEnv.formatCompileError |> String.concat "; ")
             | Ok _ ->
                 check "zarr fold: typechecks (fold succeeded)" true ""
                 match Blade.StaticEval.resolveStatics program.Modules.Head.Decls with
                 | Ok (se, _) ->
                     check "zarr fold: length(xd) = 6"
                         (Map.tryFind "n" se.Values = Some (Blade.StaticEval.SVInt 6L))
                         (sprintf "got %A" (Map.tryFind "n" se.Values))
                     check "zarr fold: prodsum(xd, xd) = 91"
                         (Map.tryFind "ps" se.Values = Some (Blade.StaticEval.SVFloat 91.0))
                         (sprintf "got %A" (Map.tryFind "ps" se.Values))
                 | Error e -> check "zarr fold: resolveStatics" false e
      with ex -> check "zarr fold: runs" false ex.Message)
     // Fold ceiling: 70000 > 65536 elements refuses with steering.
     let bigRoot = "zarr_fold_big"
     (try Directory.Delete(bigRoot, true) with _ -> ())
     ZarrWrite.writeStoreV2 bigRoot [
        { Name = "big"; DimNames = Some ["n"]; Shape = [70000L]; Chunks = [70000L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 (Array.zeroCreate 70000); OmitChunks = []; Blade = None } ]
     let bigSource = """
import zarr as z

let sample = z.load("zarr_fold_big")
let static v = sample.vars.big |> z.read
"""
     (try
         match Parser.parseProgram bigSource with
         | Error e -> check "zarr fold ceiling: parses" false e.Message
         | Ok program ->
             match TypeCheck.typeCheck program with
             | Error errs ->
                 let msg = errs |> List.map TypeEnv.formatCompileError |> String.concat "; "
                 check "zarr fold ceiling: 70000 elements refused with steering"
                     (msg.Contains "fold ceiling") msg
             | Ok _ -> check "zarr fold ceiling: 70000 elements refused with steering" false "typechecked (fold went through?)"
      with ex -> check "zarr fold ceiling: runs" false ex.Message))

    // ---------------------------------------------------------------
    // 10. Runtime dense read e2e (g++; store generated on the fly)
    // ---------------------------------------------------------------
    // Multi-chunk WITH edge chunks and one missing chunk (fill -1), so the
    // runtime path exercises key formatting, intersection copy, and fill.
    printfn "\n--- dense read e2e: method_for(z.read(s.vars.A)) <@> (x -> x+x) ---"
    let e2eDir = "./generated_cpp_tests"
    if not (Directory.Exists e2eDir) then Directory.CreateDirectory e2eDir |> ignore
    for (version, writer) in [ (2, ZarrWrite.writeStoreV2); (3, ZarrWrite.writeStoreV3) ] do
        let storeName = sprintf "zarr_e2e_v%d" version
        let storeInDir = Path.Combine(e2eDir, storeName)
        let e2eVars : ZarrWrite.WriteVar list = [
            { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [5L; 7L]; Chunks = [2L; 3L]
              FillValue = FillFloat -1.0; Data = ZarrWrite.WF64 aData; OmitChunks = [[2L; 2L]]; Blade = None } ]
        // Twice: at the compiler's cwd (compile-time metadata resolves the
        // relative path here) and beside the exe (runtime reads resolve
        // against the executable's working directory) — the same split as
        // NetcdfTests' sample.nc copy.
        (try Directory.Delete(storeName, true) with _ -> ())
        (try Directory.Delete(storeInDir, true) with _ -> ())
        writer storeName e2eVars
        writer storeInDir e2eVars
        let readSource = sprintf """
import zarr as z

let sample = z.load("%s")
let A = sample.vars.A |> z.read
let out = method_for(A) <@> lambda(x) -> x + x |> compute
"""
                             storeName
        try
            match lower readSource with
            | Ok ir ->
                check (sprintf "e2e v%d: ProviderReads spec (provider=zarr, maskless)" version)
                    (ir.Modules.[0].ProviderReads |> Map.exists (fun _ s -> s.Provider = "zarr" && s.VarName = "A" && s.MaskName = None))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir (sprintf "zarr_read_e2e_v%d" version)
                check (sprintf "e2e v%d: emits fstream reads, no netcdf dependency" version)
                    (cppCode.Contains "std::ifstream" && not (cppCode.Contains "netcdf.h")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, sprintf "zarr_read_e2e_v%d.cpp" version)
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     check (sprintf "e2e v%d: compiles (pure std C++ — no link flags)" version) true ""
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check (sprintf "e2e v%d: runs (exit 0)" version) true ""
                          // Ground truth via the F# read path (fill -1 in the
                          // omitted chunk region), kernel doubles it.
                          (match readVarData storeInDir "A" with
                           | Ok { Payload = ZFloats truth } ->
                               let expected = truth |> Array.map (fun x -> x + x)
                               let outLine =
                                   runOut.Split('\n')
                                   |> Array.tryPick (fun l ->
                                       let l = l.Trim()
                                       if l.StartsWith "out = [" && l.EndsWith "]" then Some l else None)
                               (match outLine with
                                | None -> check (sprintf "e2e v%d: values match ground truth" version) false "no out = [...] line"
                                | Some line ->
                                    let inner = line.Substring("out = [".Length, line.Length - "out = [".Length - 1)
                                    let parsed =
                                        inner.Split(',')
                                        |> Array.map (fun s -> Double.Parse(s.Trim(), Globalization.CultureInfo.InvariantCulture))
                                    let ok =
                                        parsed.Length = expected.Length
                                        && Array.forall2 (fun a b -> abs (a - b) <= 1e-9 * max 1.0 (abs b)) parsed expected
                                    check (sprintf "e2e v%d: values match ground truth (2*A incl. fill region)" version)
                                        ok
                                        (sprintf "%d vs %d values" parsed.Length expected.Length))
                           | Ok _ -> check (sprintf "e2e v%d: values match ground truth" version) false "truth not floats"
                           | Error e -> check (sprintf "e2e v%d: values match ground truth" version) false e)
                          // Missing store at runtime fails loudly (metadata check).
                          if version = 2 then
                              let missingDir = Path.Combine(Path.GetTempPath(), "blade_zarr_missing_" + Guid.NewGuid().ToString("N"))
                              Directory.CreateDirectory missingDir |> ignore
                              (try
                                  let exeCopy = Path.Combine(missingDir, Path.GetFileName exePath)
                                  File.Copy(exePath, exeCopy, true)
                                  (match runExecutable exeCopy with
                                   | Ok (code, missOut) ->
                                       check "e2e: missing store at runtime fails loudly (nonzero + Zarr error)"
                                           (code <> 0 && missOut.Contains "Zarr error")
                                           (sprintf "exit %d: %s" code (missOut.Substring(0, min 200 missOut.Length)))
                                   | Error e -> check "e2e: missing store fails loudly" false e)
                               finally
                                  try Directory.Delete(missingDir, true) with _ -> ())
                      | Ok (code, runOut) -> check (sprintf "e2e v%d: runs (exit 0)" version) false (sprintf "exit %d: %s" code runOut)
                      | Error e -> check (sprintf "e2e v%d: runs (exit 0)" version) false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP zarr read e2e v%d (compile skipped): %s" version e
                     else check (sprintf "e2e v%d: compiles" version) false e)
            | Error e -> check (sprintf "e2e v%d: lowers" version) false e
        with ex -> check (sprintf "e2e v%d" version) false ex.Message

    // ---------------------------------------------------------------
    // 11. Write -> read roundtrip e2e (the Blade-side writer)
    // ---------------------------------------------------------------
    printfn "\n--- write e2e: z.read |> z.write -> F# reads it back ---"
    (let inStore = "zarr_wrt_in"
     let outStore = "zarr_wrt_out"
     let inDirFull = Path.Combine(e2eDir, inStore)
     let outDirFull = Path.Combine(e2eDir, outStore)
     let wrtVars : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [4L; 3L]; Chunks = [2L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| for i in 0 .. 11 -> float i + 0.25 |]; OmitChunks = []; Blade = None } ]
     (try Directory.Delete(inStore, true) with _ -> ())
     (try Directory.Delete(inDirFull, true) with _ -> ())
     (try Directory.Delete(outDirFull, true) with _ -> ())
     ZarrWrite.writeStoreV3 inStore wrtVars       // compile-time metadata (compiler cwd)
     ZarrWrite.writeStoreV3 inDirFull wrtVars     // runtime read (exe cwd)
     let writeSource = sprintf """
import zarr as z

let sample = z.load("%s")
let A = sample.vars.A |> z.read
let w = z.write("%s", A)
"""
                           inStore outStore
     try
        match lower writeSource with
        | Ok ir ->
            check "write e2e: ProviderWrites spec recorded (provider=zarr)"
                (ir.Modules.[0].ProviderWrites |> Map.exists (fun _ s -> s.Provider = "zarr" && s.VarName = "A" && s.FilePath = outStore))
                (sprintf "%d write specs" (Map.count ir.Modules.[0].ProviderWrites))
            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_write_e2e"
            check "write e2e: emits flatten + filesystem writer"
                (cppCode.Contains "create_directories" && cppCode.Contains ".zarray") ""
            CodeGen.deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, "zarr_write_e2e.cpp")
            File.WriteAllText(cppFile, cppCode)
            (match compileCpp cppFile e2eDir with
             | Ok exePath ->
                 check "write e2e: compiles" true ""
                 (match runExecutable exePath with
                  | Ok (0, _) ->
                      check "write e2e: runs (exit 0)" true ""
                      (match readVarData outDirFull "A" with
                       | Ok { DimLengths = [4; 3]; Payload = ZFloats got } ->
                           check "write e2e: written store reads back exactly (F# reader)"
                               (got = [| for i in 0 .. 11 -> float i + 0.25 |]) (sprintf "got %A" got)
                       | Ok d -> check "write e2e: written store reads back" false (sprintf "shape %A" d.DimLengths)
                       | Error e -> check "write e2e: written store reads back" false e)
                      (match readVarData outDirFull "A" with
                       | Ok _ ->
                           let store = load outDirFull
                           check "write e2e: written store carries dim names (from provider index types)"
                               (match tryFindArray store "A" with
                                | Some m -> m.DimNames = Some ["x"; "y"]
                                | None -> false)
                               ""
                       | Error _ -> ())
                  | Ok (code, runOut) -> check "write e2e: runs (exit 0)" false (sprintf "exit %d: %s" code runOut)
                  | Error e -> check "write e2e: runs (exit 0)" false e)
             | Error e ->
                 if isSkipError e then printfn "  SKIP zarr write e2e (compile skipped): %s" e
                 else check "write e2e: compiles" false e)
        | Error e -> check "write e2e: lowers" false e
     with ex -> check "write e2e" false ex.Message)

    // ---------------------------------------------------------------
    // 12. load_compound rejection at codegen (zarr has no compound reader)
    // ---------------------------------------------------------------
    printfn "\n--- load_compound: loud zarr rejection ---"
    (let lcStore = "zarr_lc"
     (try Directory.Delete(lcStore, true) with _ -> ())
     ZarrWrite.writeStoreV2 lcStore [
        { Name = "A"; DimNames = Some ["x"]; Shape = [4L]; Chunks = [4L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 [| 1.0; 2.0; 3.0; 4.0 |]; OmitChunks = []; Blade = None }
        { Name = "M"; DimNames = Some ["x"]; Shape = [4L]; Chunks = [4L]
          FillValue = FillInt 0L; Data = ZarrWrite.WI64 [| 1L; 0L; 1L; 1L |]; OmitChunks = []; Blade = None } ]
     let lcSource = """
import zarr as z

let sample = z.load("zarr_lc")
let data = z.load_compound(sample.vars.A, sample.vars.M) |> z.read
"""
     match lower lcSource with
     | Ok ir ->
         (try
             CodeGen.genSelfContainedProgramFromIR ir "zarr_lc" |> ignore
             check "load_compound via zarr: rejected loudly at codegen" false "codegen succeeded?"
          with ex ->
             check "load_compound via zarr: rejected loudly at codegen"
                 (ex.Message.Contains "does not support load_compound") ex.Message)
     | Error e ->
         check "load_compound via zarr: lowers (rejection is codegen's job)" false e)

    // ---------------------------------------------------------------
    // 13. blade layout attribute: parse + validation (pure)
    // ---------------------------------------------------------------
    printfn "\n--- blade packed layout: attribute parse ---"
    let bladeZattrs extra = sprintf """{"blade": {"spec_version": 1, "layout": "packed", "order": "ascending-lex", "index_types": [%s]}}""" extra
    let v2packed shape = sprintf """{"shape":[%s],"chunks":[%s],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" shape shape
    (match parseArrayMetaV2 "C" "d" (v2packed "10") (Some (bladeZattrs """{"kind": "sym", "rank": 2, "extent": 4}""")) with
     | Ok m ->
         check "blade parse: sym r2 n4 accepted (card 10)"
             (match m.Blade with
              | Some l -> l.Group.Sym = SymSymmetric && l.Group.Rank = 2 && l.Group.Extent = 4L && l.DenseDims = []
              | None -> false)
             (sprintf "%A" m.Blade)
     | Error e -> check "blade parse: sym r2 n4 accepted (card 10)" false e)
    check "blade parse: antisym r2 n4 accepted (card 6)"
        (match parseArrayMetaV2 "C" "d" (v2packed "6") (Some (bladeZattrs """{"kind": "antisym", "rank": 2, "extent": 4}""")) with
         | Ok m -> (match m.Blade with Some l -> l.Group.Sym = SymAntisymmetric | None -> false)
         | Error _ -> false) ""
    check "blade parse: mixed sym x dense accepted"
        (match parseArrayMetaV2 "C" "d" """{"shape":[6,3],"chunks":[6,3],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}"""
                   (Some (bladeZattrs """{"kind": "sym", "rank": 2, "extent": 3}, {"kind": "dense", "extent": 3}""")) with
         | Ok m -> (match m.Blade with Some l -> l.DenseDims = [3L] | None -> false)
         | Error _ -> false) ""
    check "blade parse: cardinality mismatch is LOUD"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "9") (Some (bladeZattrs """{"kind": "sym", "rank": 2, "extent": 4}"""))) "cardinality 10") ""
    check "blade parse: herm reserved"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "10") (Some (bladeZattrs """{"kind": "herm", "rank": 2, "extent": 4}"""))) "reserved") ""
    check "blade parse: unknown kind rejected"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "10") (Some (bladeZattrs """{"kind": "diag", "rank": 2, "extent": 4}"""))) "unknown kind") ""
    check "blade parse: dense-first rejected (packed group must lead)"
        (isError (parseArrayMetaV2 "C" "d" """{"shape":[3,10],"chunks":[3,10],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}"""
                      (Some (bladeZattrs """{"kind": "dense", "extent": 3}, {"kind": "sym", "rank": 2, "extent": 4}"""))) "FIRST") ""
    check "blade parse: future spec_version rejected"
        (isError (parseArrayMetaV2 "C" "d" (v2packed "10") (Some """{"blade": {"spec_version": 2, "layout": "packed", "index_types": [{"kind": "sym", "rank": 2, "extent": 4}]}}""")) "spec_version") ""
    check "blade parse: v3 attributes carry the layout too"
        (match parseArrayMetaV3 "C" "d" """{"zarr_format":3,"node_type":"array","shape":[10],"data_type":"float64","chunk_grid":{"name":"regular","configuration":{"chunk_shape":[5]}},"fill_value":0,"codecs":[{"name":"bytes"}],"attributes":{"blade":{"spec_version":1,"layout":"packed","order":"ascending-lex","index_types":[{"kind":"sym","rank":2,"extent":4}]}}}""" with
         | Ok m -> (match m.Blade with Some l -> l.Group.Rank = 2 && l.Group.Extent = 4L | None -> false)
         | Error e -> false) ""
    check "binom/packedCardinality: sym(2,4)=10, antisym(2,4)=6, sym(3,3)=10"
        (packedCardinality { Sym = SymSymmetric; Rank = 2; Extent = 4L } = 10L
         && packedCardinality { Sym = SymAntisymmetric; Rank = 2; Extent = 4L } = 6L
         && packedCardinality { Sym = SymSymmetric; Rank = 3; Extent = 3L } = 10L) ""

    // ---------------------------------------------------------------
    // 14. Packed module typing (mirrors source-level SymIdx lowering)
    // ---------------------------------------------------------------
    printfn "\n--- blade packed layout: module typing ---"
    (let packedStore = {
        Path = "/mock/tri"; Version = 2
        Arrays = [
            { Name = "C"; ArrayDir = "/mock/tri/C"; Shape = [10L]; Chunks = [10L]
              Dtype = mockDt "f8" ETFloat64 8 true; DimNames = None
              FillValue = FillFloat 0.0; Codec = CodecIdentity
              Blade = Some { Group = { Sym = SymSymmetric; Rank = 2; Extent = 4L }; DenseDims = []; Blocks = None }
              Version = 2; ChunkKeySep = "."; ChunkKeyPrefix = "" }
        ] }
     let m = zarrStoreToModule (IRBuilder()) "tri" packedStore None
     let cType =
         m.Types |> List.tryPick (function IRTDStruct ("vars", fs) -> Some fs | _ -> None)
         |> Option.defaultValue []
         |> List.tryPick (fun (n, t) -> if n = "C" then (match t with ArrayElem at -> Some at | _ -> None) else None)
     check "packed typing: C is Array<f64 like SymIdx<2,4>> (Symmetry/Rank/Extent match source lowering)"
         (match cType with
          | Some at ->
              at.IndexTypes.Length = 1
              && at.IndexTypes.[0].Symmetry = SymSymmetric
              && at.IndexTypes.[0].Rank = 2
              && (match at.IndexTypes.[0].Extent with IRLit (IRLitInt 4L) -> true | _ -> false)
              && at.IndexTypes.[0].IxKind = IxKPlain
          | None -> false)
         (sprintf "%A" cType))

    // ---------------------------------------------------------------
    // 15-17. Packed e2e: independent oracle -> read -> compute; and
    // read -> write roundtrip preserving exact pool order + metadata.
    // The oracle pool is computed with an INDEPENDENT F# enumeration
    // (ascending-lex loops here, not shared with provider code).
    // ---------------------------------------------------------------
    printfn "\n--- blade packed e2e: read + write roundtrips (sym and antisym) ---"
    let triOracle (strict: bool) (n: int) : float[] =
        [| for i in 0 .. n - 1 do
             for j in (if strict then i + 1 else i) .. n - 1 ->
               float ((i + 1) * 10 + (j + 1)) |]
    // Fold rejection first (hermetic, no g++): packed vars refuse to fold.
    (let foldTri = "zarr_tri_foldreject"
     (try Directory.Delete(foldTri, true) with _ -> ())
     ZarrWrite.writeStoreV2 foldTri [
        { Name = "C"; DimNames = None; Shape = [10L]; Chunks = [10L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 (triOracle false 4); OmitChunks = []
          Blade = Some { Group = { Sym = SymSymmetric; Rank = 2; Extent = 4L }; DenseDims = []; Blocks = None } } ]
     match Blade.ProviderRegistry.tryFind "zarr" with
     | Some s ->
         check "packed fold: refused with steering"
             (match s.ReadVarData foldTri "C" with
              | Error e -> e.Contains "do not fold"
              | Ok _ -> false) ""
     | None -> check "packed fold: registry has zarr" false "")
    for (kind, sym, strict) in [ ("sym", SymSymmetric, false); ("antisym", SymAntisymmetric, true) ] do
        let n = 4
        let pool = triOracle strict n
        let card = int64 pool.Length
        let inStore = sprintf "zarr_tri_%s" kind
        let outStore = sprintf "zarr_tri_%s_out" kind
        let layout : BladeLayout = { Group = { Sym = sym; Rank = 2; Extent = int64 n }; DenseDims = []; Blocks = None }
        let triVars : ZarrWrite.WriteVar list = [
            { Name = "C"; DimNames = None; Shape = [card]; Chunks = [card]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        (try Directory.Delete(inStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        ZarrWrite.writeStoreV2 inStore triVars
        ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) triVars
        let triSource = sprintf """
import zarr as z

let s = z.load("%s")
let C = s.vars.C |> z.read
let out = method_for(C) <@> lambda(x) -> x + x |> compute
let w = z.write("%s", C)
"""
                            inStore outStore
        try
            match lower triSource with
            | Ok ir ->
                check (sprintf "packed %s: read spec is packed (provider=zarr)" kind)
                    (ir.Modules.[0].ProviderReads |> Map.exists (fun _ s ->
                        s.Provider = "zarr" && s.VarType.IndexTypes |> List.exists (fun ix -> ix.Symmetry = sym && ix.Rank = 2)))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir (sprintf "zarr_tri_%s_e2e" kind)
                check (sprintf "packed %s: codegen materializes the packed pool" kind)
                    (if strict then
                        // Antisym: dead-diagonal host pool -> unrank + relative subscripts.
                        cppCode.Contains "antisymmetric::unlinearize" && cppCode.Contains "_anti"
                     else
                        // Sym: compact pool -> linear pool_base copy under a hoisted SYMM.
                        cppCode.Contains "_symm" && cppCode.Contains "pool_base") ""
                check (sprintf "packed %s: linearized_storage header included" kind)
                    (cppCode.Contains "linearized_storage.hpp") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, sprintf "zarr_tri_%s_e2e.cpp" kind)
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     check (sprintf "packed %s: compiles" kind) true ""
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check (sprintf "packed %s: runs (exit 0)" kind) true ""
                          // The doubled kernel output must cover exactly the
                          // doubled pool VALUES (set equality — print order of
                          // a packed array is a codegen concern, the exact
                          // POOL order is pinned by the write roundtrip below).
                          let outLine =
                              runOut.Split('\n')
                              |> Array.tryPick (fun l ->
                                  let l = l.Trim()
                                  if l.StartsWith "out = [" && l.EndsWith "]" then Some l else None)
                          (match outLine with
                           | Some line ->
                               let inner = line.Substring("out = [".Length, line.Length - "out = [".Length - 1)
                               let got =
                                   inner.Split(',')
                                   |> Array.map (fun s -> Double.Parse(s.Trim(), Globalization.CultureInfo.InvariantCulture))
                                   |> Set.ofArray
                               let expected = pool |> Array.map (fun x -> x + x) |> Set.ofArray
                               check (sprintf "packed %s: kernel values = 2x oracle pool (set)" kind)
                                   (Set.isSubset expected got && Set.isSubset (Set.remove 0.0 got) expected)
                                   (sprintf "got %A expected %A" got expected)
                           | None -> check (sprintf "packed %s: kernel values" kind) false "no out = [...] line")
                          // Write roundtrip: exact pool order + blade metadata.
                          let outFull = Path.Combine(e2eDir, outStore)
                          (match readVarData outFull "C" with
                           | Ok { Payload = ZFloats got } ->
                               check (sprintf "packed %s: written pool is EXACTLY the input pool (canonical order preserved)" kind)
                                   (got = pool) (sprintf "got %A" (Array.truncate 6 got))
                           | Ok _ -> check (sprintf "packed %s: written pool exact" kind) false "not floats"
                           | Error e -> check (sprintf "packed %s: written pool exact" kind) false e)
                          (try
                              let wstore = load outFull
                              check (sprintf "packed %s: written store carries the blade attribute" kind)
                                  (match tryFindArray wstore "C" with
                                   | Some m -> m.Blade = Some layout
                                   | None -> false) ""
                           with ex -> check (sprintf "packed %s: written store loads" kind) false ex.Message)
                      | Ok (code, runOut) -> check (sprintf "packed %s: runs (exit 0)" kind) false (sprintf "exit %d: %s" code runOut)
                      | Error e -> check (sprintf "packed %s: runs (exit 0)" kind) false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP packed %s e2e (compile skipped): %s" kind e
                     else check (sprintf "packed %s: compiles" kind) false e)
            | Error e -> check (sprintf "packed %s: lowers" kind) false e
        with ex -> check (sprintf "packed %s e2e" kind) false ex.Message

    // ---------------------------------------------------------------
    // 18. Mixed sym x dense packed read -> write roundtrip
    // ---------------------------------------------------------------
    printfn "\n--- blade packed e2e: mixed sym x dense ---"
    (let n = 3
     let trail = 2
     let symCells = [ for i in 0 .. n - 1 do for j in i .. n - 1 -> (i, j) ]
     let pool =
         [| for (i, j) in symCells do
              for t in 0 .. trail - 1 ->
                float (100 * (i + 1) + 10 * (j + 1) + t) |]
     let card = int64 symCells.Length
     let layout : BladeLayout = { Group = { Sym = SymSymmetric; Rank = 2; Extent = int64 n }; DenseDims = [int64 trail]; Blocks = None }
     let mixVars : ZarrWrite.WriteVar list = [
        { Name = "D"; DimNames = Some ["cells"; "t"]; Shape = [card; int64 trail]; Chunks = [card; int64 trail]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
          Blade = Some layout } ]
     let inStore = "zarr_tri_mixed"
     let outStore = "zarr_tri_mixed_out"
     (try Directory.Delete(inStore, true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
     ZarrWrite.writeStoreV2 inStore mixVars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) mixVars
     let mixSource = sprintf """
import zarr as z

let s = z.load("%s")
let D = s.vars.D |> z.read
let w = z.write("%s", D)
"""
                         inStore outStore
     try
        match lower mixSource with
        | Ok ir ->
            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_tri_mixed_e2e"
            CodeGen.deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, "zarr_tri_mixed_e2e.cpp")
            File.WriteAllText(cppFile, cppCode)
            (match compileCpp cppFile e2eDir with
             | Ok exePath ->
                 (match runExecutable exePath with
                  | Ok (0, _) ->
                      check "packed mixed: runs (exit 0)" true ""
                      (match readVarData (Path.Combine(e2eDir, outStore)) "D" with
                       | Ok { DimLengths = dl; Payload = ZFloats got } ->
                           check "packed mixed: pool x trailing roundtrips exactly"
                               (dl = [int card; trail] && got = pool) (sprintf "shape %A" dl)
                       | Ok _ -> check "packed mixed: roundtrip" false "not floats"
                       | Error e -> check "packed mixed: roundtrip" false e)
                  | Ok (code, out) -> check "packed mixed: runs (exit 0)" false (sprintf "exit %d: %s" code out)
                  | Error e -> check "packed mixed: runs (exit 0)" false e)
             | Error e ->
                 if isSkipError e then printfn "  SKIP packed mixed e2e (compile skipped): %s" e
                 else check "packed mixed: compiles" false e)
        | Error e -> check "packed mixed: lowers" false e
     with ex -> check "packed mixed e2e" false ex.Message)

    // ---------------------------------------------------------------
    // 19. Simplex-blocks: math identities (Phase 0, pure)
    // ---------------------------------------------------------------
    printfn "\n--- simplex-blocks: math identities ---"
    (let roundtrip strict n r =
        let count = if strict then binom n r else binom (n + int64 r - 1L) r
        seq { 0L .. count - 1L }
        |> Seq.forall (fun rank ->
            let c = SimplexBlocks.unrankToCoords strict n r rank
            SimplexBlocks.rankOfCoords strict n c = rank)
     check "rank/unrank roundtrip: sym n=5 r=3 (35 cells)" (roundtrip false 5L 3) ""
     check "rank/unrank roundtrip: antisym n=6 r=3 (20 cells)" (roundtrip true 6L 3) "")
    (let sumCells strict n B r =
        let T = (n + B - 1L) / B
        seq { 0L .. SimplexBlocks.blockCount r T - 1L }
        |> Seq.sumBy (fun b ->
            SimplexBlocks.blockCellCount strict n B (SimplexBlocks.unrankToCoords false T r b))
     check "block cells sum to cardinality: sym n=5 B=2 r=2 (ragged tile)"
         (sumCells false 5L 2L 2 = 15L) (sprintf "%d" (sumCells false 5L 2L 2))
     check "block cells sum to cardinality: antisym n=5 B=2 r=2"
         (sumCells true 5L 2L 2 = 10L) (sprintf "%d" (sumCells true 5L 2L 2))
     check "block cells sum to cardinality: antisym n=4 B=1 r=2 (diagonal blocks EMPTY)"
         (sumCells true 4L 1L 2 = 6L) (sprintf "%d" (sumCells true 4L 1L 2)))
    check "antisym B=1: every repeated-tile block is empty (the diagonal specialness)"
        (seq { 0L .. SimplexBlocks.blockCount 2 4L - 1L }
         |> Seq.forall (fun b ->
             let tiles = SimplexBlocks.unrankToCoords false 4L 2 b
             let cells = SimplexBlocks.blockCellCount true 4L 1L tiles
             if tiles.[0] = tiles.[1] then cells = 0L else cells = 1L)) ""
    check "maxBlockCells bounds every block (sym + antisym, n=7 B=3 r=3)"
        (let T = 3L
         seq { 0L .. SimplexBlocks.blockCount 3 T - 1L }
         |> Seq.forall (fun b ->
             let tiles = SimplexBlocks.unrankToCoords false T 3 b
             SimplexBlocks.blockCellCount false 7L 3L tiles <= SimplexBlocks.maxBlockCells 3 3L
             && SimplexBlocks.blockCellCount true 7L 3L tiles <= SimplexBlocks.maxBlockCells 3 3L)) ""
    check "enumBlockCells: counts match closed form + cells canonical (sym n=5 B=2 r=2)"
        (let T = 3L
         seq { 0L .. SimplexBlocks.blockCount 2 T - 1L }
         |> Seq.forall (fun b ->
             let tiles = SimplexBlocks.unrankToCoords false T 2 b
             let cells = SimplexBlocks.enumBlockCells false 5L 2L tiles |> List.ofSeq
             int64 cells.Length = SimplexBlocks.blockCellCount false 5L 2L tiles
             && cells |> List.forall (fun c -> c.[0] <= c.[1]))) ""
    (let bijective strict n B r order =
        let map = SimplexBlocks.blocksCellMap strict n B r order
        let hits = map |> Array.filter (fun p -> p >= 0)
        let card = if strict then binom n r else binom (n + int64 r - 1L) r
        int64 hits.Length = card && (hits |> Array.sort) = [| 0 .. int card - 1 |]
     check "blocksCellMap: bijection onto the pool (sym n=5 B=2)" (bijective false 5L 2L 2 OrderLex) ""
     check "blocksCellMap: bijection onto the pool (antisym n=4 B=1, empty rows)" (bijective true 4L 1L 2 OrderLex) ""
     check "blocksCellMap: bijection under PATH order (sym n=8 B=2, T=4)" (bijective false 8L 2L 2 OrderPath) "")
    check "pathRows: a permutation (r=2, T=4)"
        (let rows = SimplexBlocks.pathRows 2 4L
         (rows |> Array.sort) = [| 0L .. int64 rows.Length - 1L |]) ""
    check "pathRows: non-power-of-two grid refused"
        (try SimplexBlocks.pathRows 2 3L |> ignore; false
         with ex -> ex.Message.Contains "power-of-two") ""
    (let (phys, pool) = SimplexBlocks.paddingReport false 100L 16L 2
     printfn "  INFO: padding sym n=100 B=16 r=2: physical %d cells vs pool %d (%.1f%% overhead)"
         phys pool (100.0 * float (phys - pool) / float pool))

    // ---------------------------------------------------------------
    // 20. Simplex-blocks: store I/O (Phase 1)
    // ---------------------------------------------------------------
    printfn "\n--- simplex-blocks: store write -> load -> pool roundtrip ---"
    let sbLayout sym strict rank n tile order : BladeLayout =
        let T = (n + tile - 1L) / tile
        { Group = { Sym = sym; Rank = rank; Extent = n }
          DenseDims = []
          Blocks = Some { Tile = tile; Grid = T; Order = order } }
    let sbPool strict n =
        triOracle strict (int n)
    let sbRoundtrip (name: string) (sym, strict) (n: int64) (tile: int64) order (writer: string -> ZarrWrite.WriteVar list -> unit) =
        let layout = sbLayout sym strict 2 n tile order
        let pool = sbPool strict n
        let root = Path.Combine(scratch, name)
        (try Directory.Delete(root, true) with _ -> ())
        writer root [
            { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
              FillValue = FillFloat -9.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        let store = load root
        match tryFindArray store "C" with
        | None -> check (sprintf "%s: array found" name) false ""
        | Some m ->
            check (sprintf "%s: physical shape is [blockCount, tile^r]" name)
                (match m.Blade with
                 | Some l -> l.Blocks.IsSome && m.Shape.Length = 2
                 | None -> false)
                (sprintf "%A" m.Shape)
            (match readPackedPool m with
             | Ok { DimLengths = [len]; Payload = ZFloats got } ->
                 check (sprintf "%s: pool roundtrips exactly through block rows" name)
                     (len = pool.Length && got = pool) (sprintf "len %d" len)
             | Ok d -> check (sprintf "%s: pool roundtrips" name) false (sprintf "%A" d.DimLengths)
             | Error e -> check (sprintf "%s: pool roundtrips" name) false e)
    sbRoundtrip "sb_sym_ragged_v2" (SymSymmetric, false) 5L 2L OrderLex ZarrWrite.writeStoreV2
    sbRoundtrip "sb_antisym_ragged_v2" (SymAntisymmetric, true) 5L 2L OrderLex ZarrWrite.writeStoreV2
    sbRoundtrip "sb_antisym_B1_v2" (SymAntisymmetric, true) 4L 1L OrderLex ZarrWrite.writeStoreV2
    sbRoundtrip "sb_sym_path_v3" (SymSymmetric, false) 8L 2L OrderPath ZarrWrite.writeStoreV3
    // Differential: same pool via flat "packed" and via blocks reads identically.
    (let pool = sbPool false 5L
     let flatRoot = Path.Combine(scratch, "sb_diff_flat")
     (try Directory.Delete(flatRoot, true) with _ -> ())
     ZarrWrite.writeStoreV2 flatRoot [
        { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
          Blade = Some { Group = { Sym = SymSymmetric; Rank = 2; Extent = 5L }; DenseDims = []; Blocks = None } } ]
     let flatPool =
         match load flatRoot |> fun s -> tryFindArray s "C" |> Option.get |> readPackedPool with
         | Ok { Payload = ZFloats xs } -> xs
         | _ -> [||]
     let blocksPool =
         match load (Path.Combine(scratch, "sb_sym_ragged_v2")) |> fun s -> tryFindArray s "C" |> Option.get |> readPackedPool with
         | Ok { Payload = ZFloats xs } -> xs
         | _ -> [||]
     check "differential: flat-packed and simplex-blocks stores read the SAME pool"
         (flatPool = pool && blocksPool = pool) "")
    // Mixed trailing dims through block rows.
    (let n = 3L
     let trail = 2
     let symCells = [ for i in 0 .. int n - 1 do for j in i .. int n - 1 -> (i, j) ]
     let pool = [| for (i, j) in symCells do for t in 0 .. trail - 1 -> float (100 * (i + 1) + 10 * (j + 1) + t) |]
     let layout = { Group = { Sym = SymSymmetric; Rank = 2; Extent = n }
                    DenseDims = [int64 trail]
                    Blocks = Some { Tile = 2L; Grid = 2L; Order = OrderLex } }
     let root = Path.Combine(scratch, "sb_mixed")
     (try Directory.Delete(root, true) with _ -> ())
     ZarrWrite.writeStoreV2 root [
        { Name = "D"; DimNames = None; Shape = [int64 symCells.Length; int64 trail]; Chunks = [int64 symCells.Length; int64 trail]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
          Blade = Some layout } ]
     match load root |> fun s -> tryFindArray s "D" |> Option.get |> readPackedPool with
     | Ok { DimLengths = [6; 2]; Payload = ZFloats got } ->
         check "mixed sym x dense through block rows roundtrips" (got = pool) ""
     | Ok d -> check "mixed sym x dense through block rows roundtrips" false (sprintf "%A" d.DimLengths)
     | Error e -> check "mixed sym x dense through block rows roundtrips" false e)
    // Parse rejections.
    (let blocksAttr tile grid extra =
        sprintf """{"blade": {"spec_version": 1, "layout": "packed-blocks", "order": "ascending-lex", "index_types": [{"kind": "sym", "rank": 2, "extent": 5}], "decomposition": {"scheme": "simplex-blocks", "tile": %d, "grid": %d%s}}}""" tile grid extra
     let v2phys shape = sprintf """{"shape":[%s],"chunks":[%s],"dtype":"<f8","compressor":null,"fill_value":0,"order":"C","filters":null}""" shape shape
     check "blocks parse: good store accepted (shape [6,4], n=5 B=2)"
         (match parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some (blocksAttr 2 3 "")) with
          | Ok m -> (match m.Blade with Some l -> l.Blocks = Some { Tile = 2L; Grid = 3L; Order = OrderLex } | None -> false)
          | Error _ -> false) ""
     check "blocks parse: wrong physical shape LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "5, 4") (Some (blocksAttr 2 3 ""))) "does not match [blockCount") ""
     check "blocks parse: grid/tile mismatch LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some (blocksAttr 2 4 ""))) "does not match ceil") ""
     check "blocks parse: path order on non-power-of-two grid LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some (blocksAttr 2 3 ", \"block_order\": \"path\""))) "power-of-two") ""
     check "blocks parse: unknown scheme LOUD"
         (isError (parseArrayMetaV2 "C" "d" (v2phys "6, 4") (Some """{"blade": {"spec_version": 1, "layout": "packed-blocks", "index_types": [{"kind": "sym", "rank": 2, "extent": 5}], "decomposition": {"scheme": "hilbert", "tile": 2, "grid": 3}}}""")) "hilbert") "")

    // ---------------------------------------------------------------
    // 21. Simplex-blocks: runtime read e2e (Phase 2)
    // ---------------------------------------------------------------
    // A Blade program reads a BLOCKS store and writes a flat "packed" store;
    // F# compares the pools exactly — pinning the emitted per-block
    // reassembly (tile unrank + branch-free bounds + linearize) end to end.
    printfn "\n--- simplex-blocks: runtime read e2e ---"
    let sbE2E (name: string) (sym, strict) (n: int64) (tile: int64) order =
        let layout = sbLayout sym strict 2 n tile order
        let pool = sbPool strict n
        let inStore = sprintf "zarr_sb_%s" name
        let outStore = sprintf "zarr_sb_%s_out" name
        let vars : ZarrWrite.WriteVar list = [
            { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
              FillValue = FillFloat -9.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        (try Directory.Delete(inStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        ZarrWrite.writeStoreV2 inStore vars
        ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) vars
        let src = sprintf """
import zarr as z

let s = z.load("%s")
let C = s.vars.C |> z.read
let w = z.write("%s", C)
"""
                      inStore outStore
        try
            match lower src with
            | Ok ir ->
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir (sprintf "zarr_sb_%s_e2e" name)
                check (sprintf "sb e2e %s: emits per-block reassembly" name)
                    (cppCode.Contains "simplex-blocks" && cppCode.Contains "symmetric::unlinearize") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, sprintf "zarr_sb_%s_e2e.cpp" name)
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, _) ->
                          check (sprintf "sb e2e %s: runs (exit 0)" name) true ""
                          (match readVarData (Path.Combine(e2eDir, outStore)) "C" with
                           | Ok { Payload = ZFloats got } ->
                               check (sprintf "sb e2e %s: pool through C++ blocks read == oracle pool" name)
                                   (got = pool) (sprintf "got %A" (Array.truncate 6 got))
                           | Ok _ -> check (sprintf "sb e2e %s: pool matches" name) false "not floats"
                           | Error e -> check (sprintf "sb e2e %s: pool matches" name) false e)
                      | Ok (code, out) -> check (sprintf "sb e2e %s: runs (exit 0)" name) false (sprintf "exit %d: %s" code out)
                      | Error e -> check (sprintf "sb e2e %s: runs (exit 0)" name) false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP sb e2e %s (compile skipped): %s" name e
                     else check (sprintf "sb e2e %s: compiles" name) false e)
            | Error e -> check (sprintf "sb e2e %s: lowers" name) false e
        with ex -> check (sprintf "sb e2e %s" name) false ex.Message
    sbE2E "sym" (SymSymmetric, false) 5L 2L OrderLex
    sbE2E "antisym" (SymAntisymmetric, true) 5L 2L OrderLex
    sbE2E "path" (SymSymmetric, false) 8L 2L OrderPath

    // ---------------------------------------------------------------
    // 22. Window reads: z.read_window(var, lo, hi) (Phase 3b)
    // ---------------------------------------------------------------
    printfn "\n--- read_window: sub-simplex window reads ---"
    (let n = 6L
     let winPool =
         [| for i in 2 .. 5 do
              for j in i .. 5 ->
                float ((i + 1) * 10 + (j + 1)) |]
     for (label, blocks) in [ ("blocks", Some { Tile = 2L; Grid = 3L; Order = OrderLex }); ("flat", None) ] do
        let layout : BladeLayout = { Group = { Sym = SymSymmetric; Rank = 2; Extent = n }; DenseDims = []; Blocks = blocks }
        let pool = sbPool false n
        let inStore = sprintf "zarr_win_%s" label
        let outStore = sprintf "zarr_win_%s_out" label
        let vars : ZarrWrite.WriteVar list = [
            { Name = "C"; DimNames = None; Shape = [int64 pool.Length]; Chunks = [int64 pool.Length]
              FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 pool; OmitChunks = []
              Blade = Some layout } ]
        (try Directory.Delete(inStore, true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
        (try Directory.Delete(Path.Combine(e2eDir, outStore), true) with _ -> ())
        ZarrWrite.writeStoreV2 inStore vars
        ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) vars
        let src = sprintf """
import zarr as z

let s = z.load("%s")
let W = z.read_window(s.vars.C, 2, 6)
let w = z.write("%s", W)
"""
                      inStore outStore
        try
            match lower src with
            | Ok ir ->
                check (sprintf "window %s: spec carries Window=(2,6) and the WINDOW type (extent 4)" label)
                    (ir.Modules.[0].ProviderReads |> Map.exists (fun _ s ->
                        s.Window = Some (2L, 6L)
                        && (match s.VarType.IndexTypes with
                            | lead :: _ -> (match lead.Extent with IRLit (IRLitInt 4L) -> lead.Symmetry = SymSymmetric | _ -> false)
                            | [] -> false)))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir (sprintf "zarr_win_%s_e2e" label)
                check (sprintf "window %s: emits the extraction pass" label)
                    (cppCode.Contains "window [2, 6) extraction") ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, sprintf "zarr_win_%s_e2e.cpp" label)
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     (match runExecutable exePath with
                      | Ok (0, _) ->
                          check (sprintf "window %s: runs (exit 0)" label) true ""
                          (match readVarData (Path.Combine(e2eDir, outStore)) "W" with
                           | Ok { Payload = ZFloats got } ->
                               check (sprintf "window %s: window pool == oracle sub-simplex (translated SymIdx<2,4>)" label)
                                   (got = winPool) (sprintf "got %A" got)
                           | Ok _ -> check (sprintf "window %s: window pool" label) false "not floats"
                           | Error e -> check (sprintf "window %s: window pool" label) false e)
                          (try
                              let ws = load (Path.Combine(e2eDir, outStore))
                              check (sprintf "window %s: written window store types as SymIdx<2,4>" label)
                                  (match tryFindArray ws "W" with
                                   | Some m -> (match m.Blade with Some l -> l.Group.Extent = 4L | None -> false)
                                   | None -> false) ""
                           with ex -> check (sprintf "window %s: out store loads" label) false ex.Message)
                      | Ok (code, out) -> check (sprintf "window %s: runs (exit 0)" label) false (sprintf "exit %d: %s" code out)
                      | Error e -> check (sprintf "window %s: runs (exit 0)" label) false e)
                 | Error e ->
                     if isSkipError e then printfn "  SKIP window %s e2e (compile skipped): %s" label e
                     else check (sprintf "window %s: compiles" label) false e)
            | Error e -> check (sprintf "window %s: lowers" label) false e
        with ex -> check (sprintf "window %s e2e" label) false ex.Message)
    check "window: out-of-range bounds rejected at typecheck"
        ((typeErrOf "import zarr as z\nlet s = z.load(\"zarr_win_blocks\")\nlet W = z.read_window(s.vars.C, 2, 7)\n").Contains "bounds")
        (typeErrOf "import zarr as z\nlet s = z.load(\"zarr_win_blocks\")\nlet W = z.read_window(s.vars.C, 2, 7)\n")
    check "window: dense variables rejected with steering"
        ((typeErrOf "import zarr as z\nlet s = z.load(\"zarr_e2e_v2\")\nlet W = z.read_window(s.vars.A, 0, 2)\n").Contains "PACKED")
        (typeErrOf "import zarr as z\nlet s = z.load(\"zarr_e2e_v2\")\nlet W = z.read_window(s.vars.A, 0, 2)\n")

    // ---------------------------------------------------------------
    // 23. MPI-distributed packed read (Phase 3a; needs mpiexec, skips
    // gracefully). Differential: serial build (gate off) vs mpiexec -n 1/3
    // (gate on) — identical stdout, identical written pool, and the mpi
    // build's read is genuinely rank-scoped (Allgatherv restoration).
    // ---------------------------------------------------------------
    printfn "\n--- zarr mpi: distributed simplex-blocks read (differential) ---"
    (let mpiPool = sbPool false 5L
     let mpiLayout : BladeLayout =
         { Group = { Sym = SymSymmetric; Rank = 2; Extent = 5L }; DenseDims = []
           Blocks = Some { Tile = 2L; Grid = 3L; Order = OrderLex } }
     let aData = [| for i in 0 .. 11 -> float i * 0.5 |]
     let mpiVars : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["x"; "y"]; Shape = [4L; 3L]; Chunks = [4L; 3L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 aData; OmitChunks = []; Blade = None }
        { Name = "C"; DimNames = None; Shape = [int64 mpiPool.Length]; Chunks = [int64 mpiPool.Length]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 mpiPool; OmitChunks = []
          Blade = Some mpiLayout } ]
     let inStore = "zarr_mpi_in"
     let outStore = "zarr_mpi_out"
     let outFull = Path.Combine(e2eDir, outStore)
     (try Directory.Delete(inStore, true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, inStore), true) with _ -> ())
     ZarrWrite.writeStoreV2 inStore mpiVars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, inStore)) mpiVars
     let src = sprintf """
import zarr as z

let s = z.load("%s")
let A = s.vars.A |> z.read
let C = s.vars.C |> z.read
let R = method_for(A) <@> lambda(x) where mpi -> x * 2.0 |> compute
let w = z.write("%s", C)
"""
                   inStore outStore
     if Blade.Build.mpiexecPath.Value.IsNone then
         printfn "  SKIP zarr mpi differential: mpiexec not found"
     else
     try
        try
            // Serial reference (emit gate OFF: the mpi clause is inert).
            CodeGen.setMpiEmitMode false
            let serialOut =
                match lower src with
                | Ok ir ->
                    let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_mpi_ref"
                    CodeGen.deployRuntimeHeaders e2eDir
                    let f = Path.Combine(e2eDir, "zarr_mpi_ref.cpp")
                    File.WriteAllText(f, cpp)
                    (match compileCpp f e2eDir with
                     | Ok exe ->
                         (try Directory.Delete(outFull, true) with _ -> ())
                         (match runExecutable exe with
                          | Ok (0, out) -> Some out
                          | _ -> None)
                     | Error _ -> None)
                | Error _ -> None
            match serialOut with
            | None -> printfn "  SKIP zarr mpi differential: serial reference build failed"
            | Some refOut ->
                (match readVarData outFull "C" with
                 | Ok { Payload = ZFloats got } ->
                     check "zarr mpi: serial reference writes the oracle pool" (got = mpiPool) ""
                 | _ -> check "zarr mpi: serial reference writes the oracle pool" false "")
                // MPI build (emit gate ON).
                CodeGen.setMpiEmitMode true
                match lower src with
                | Error e -> check "zarr mpi: lowers under the emit gate" false e
                | Ok ir ->
                    let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "zarr_mpi_e2e"
                    check "zarr mpi: emits the distributed read (rank-scoped + Allgatherv)"
                        (cpp.Contains "distributed simplex-blocks read" && cpp.Contains "MPI_Allgatherv") ""
                    check "zarr mpi: provider write is rank-0 guarded"
                        (cpp.Contains "provider write: rank 0 only") ""
                    CodeGen.deployRuntimeHeaders e2eDir
                    let f = Path.Combine(e2eDir, "zarr_mpi_e2e.cpp")
                    File.WriteAllText(f, cpp)
                    (match compileCpp f e2eDir with
                     | Ok exe ->
                         for ranks in [1; 3] do
                             (try Directory.Delete(outFull, true) with _ -> ())
                             (match runExecutableMpi ranks exe with
                              | Ok (0, out) ->
                                  // Drop the wall-clock timing line — it differs
                                  // by nature between any two runs.
                                  let normalize (s: string) =
                                      s.Split('\n')
                                      |> Array.filter (fun l -> not (l.Contains "completed in"))
                                      |> Array.map (fun l -> l.TrimEnd())
                                      |> String.concat "\n"
                                      |> fun x -> x.Trim()
                                  check (sprintf "zarr mpi -n %d: stdout identical to serial" ranks)
                                      (normalize out = normalize refOut)
                                      (sprintf "mpi: %s" (out.Substring(0, min 200 out.Length)))
                                  (match readVarData outFull "C" with
                                   | Ok { Payload = ZFloats got } ->
                                       check (sprintf "zarr mpi -n %d: gathered pool == oracle (write from rank 0)" ranks)
                                           (got = mpiPool) ""
                                   | _ -> check (sprintf "zarr mpi -n %d: gathered pool == oracle" ranks) false "read-back failed")
                              | Ok (code, out) -> check (sprintf "zarr mpi -n %d: runs (exit 0)" ranks) false (sprintf "exit %d: %s" code (out.Substring(0, min 200 out.Length)))
                              | Error e -> check (sprintf "zarr mpi -n %d: runs (exit 0)" ranks) false e)
                     | Error e ->
                         if isSkipError e then printfn "  SKIP zarr mpi e2e (compile skipped): %s" e
                         else check "zarr mpi: compiles" false e)
        finally
            CodeGen.setMpiEmitMode false
     with ex -> check "zarr mpi differential" false ex.Message)

    // ---------------------------------------------------------------
    // 24. Streaming reads (z.stream): fiber reads inlined at the S/T
    // boundary. THE gate is differential: the same program with `.read`
    // (materialize) and `.stream` must produce identical stdout, while the
    // streamed build must show the in-nest fiber reads and NO whole-array
    // materialization.
    // ---------------------------------------------------------------
    printfn "\n--- z.stream: inline fiber reads (differential vs .read) ---"
    (let strmData = [| for i in 0 .. 11 -> float ((i * 7) % 13) + 0.5 |]
     let strmVars : ZarrWrite.WriteVar list = [
        { Name = "A"; DimNames = Some ["s"; "t"]; Shape = [4L; 3L]; Chunks = [2L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 strmData; OmitChunks = []; Blade = None } ]
     (try Directory.Delete("zarr_strm", true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, "zarr_strm"), true) with _ -> ())
     ZarrWrite.writeStoreV2 "zarr_strm" strmVars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, "zarr_strm")) strmVars
     // 2D-site store for the fused joint-symmetry case.
     let strm2Data = [| for i in 0 .. 17 -> float ((i * 5) % 11) + 0.25 |]
     let strm2Vars : ZarrWrite.WriteVar list = [
        { Name = "B"; DimNames = Some ["p"; "q"; "t"]; Shape = [3L; 2L; 3L]; Chunks = [2L; 2L; 2L]
          FillValue = FillFloat 0.0; Data = ZarrWrite.WF64 strm2Data; OmitChunks = []; Blade = None } ]
     (try Directory.Delete("zarr_strm2", true) with _ -> ())
     (try Directory.Delete(Path.Combine(e2eDir, "zarr_strm2"), true) with _ -> ())
     ZarrWrite.writeStoreV2 "zarr_strm2" strm2Vars
     ZarrWrite.writeStoreV2 (Path.Combine(e2eDir, "zarr_strm2")) strm2Vars

     // Compare COMPUTE outputs only: the .read build additionally prints
     // the materialized source array (A/B = [...]), which the streamed
     // build correctly has nothing to print. The timing line differs by
     // nature.
     let normalize (s: string) =
         s.Split('\n')
         |> Array.filter (fun l ->
             not (l.Contains "completed in")
             && not (l.TrimStart().StartsWith "A = [")
             && not (l.TrimStart().StartsWith "B = ["))
         |> Array.map (fun l -> l.TrimEnd())
         |> String.concat "\n"
         |> fun x -> x.Trim()
     let compileRun (testName: string) (src: string) : Result<string * string, string> =
         match lower src with
         | Error e -> Error (sprintf "lower: %s" e)
         | Ok ir ->
             let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir testName
             CodeGen.deployRuntimeHeaders e2eDir
             let f = Path.Combine(e2eDir, testName + ".cpp")
             File.WriteAllText(f, cpp)
             match compileCpp f e2eDir with
             | Error e -> Error (sprintf "compile: %s" e)
             | Ok exe ->
                 match runExecutable exe with
                 | Ok (0, out) -> Ok (out, cpp)
                 | Ok (code, out) -> Error (sprintf "exit %d: %s" code (out.Substring(0, min 300 out.Length)))
                 | Error e -> Error e
     let differential (label: string) (mkSrc: string -> string) =
         match compileRun (sprintf "strm_%s_read" label) (mkSrc "read") with
         | Error e ->
             printfn "  SKIP stream differential %s: .read baseline failed (%s)" label e
         | Ok (refOut, _) ->
             (match compileRun (sprintf "strm_%s_stream" label) (mkSrc "stream") with
              | Error e -> check (sprintf "stream %s: streamed build runs" label) false e
              | Ok (out, cpp) ->
                  check (sprintf "stream %s: stdout identical to .read" label)
                      (normalize out = normalize refOut)
                      (sprintf "stream: %s / read: %s" (normalize out) (normalize refOut))
                  check (sprintf "stream %s: fiber buffers + stream prologue emitted" label)
                      (cpp.Contains "_fb_p" && cpp.Contains "// Stream ") "")

     // (b) cov-like: comm pair over 1D sites, SymIdx<2,4> output.
     differential "cov" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("zarr_strm")
let A = sd.vars.A |> z.%s
let m2 = method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0 |> compute
"""
                                         verb)
     // (c) skew-like: comm triple, SymIdx<3,4> output.
     differential "skew" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("zarr_strm")
let A = sd.vars.A |> z.%s
let m3 = method_for(A, A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>, z2: Array<Float64 like TimeIdx>) where comm(x, y, z2) -> prodsum(x, y, z2) / 3.0 |> compute
"""
                                         verb)
     // (a) mean-like: arity-1 fiber map (skips with a note if the shape
     // isn't supported by the .read baseline either).
     differential "mean" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("zarr_strm")
let A = sd.vars.A |> z.%s
let mu = method_for(A) <@> lambda(x: Array<Float64 like TimeIdx>) -> prodsum(x, x) / 3.0 |> compute
"""
                                         verb)
     // (d) 2D sites + time: the user's headline case — joint-symmetric cov
     // over (p, q) sites (fused compound level) with streamed fibers.
     differential "cov2d" (fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("zarr_strm2")
let B = sd.vars.B |> z.%s
let m2 = method_for(B, B) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0 |> compute
"""
                                         verb)
     // (g) FUSED trees over provider I/O — the <&>/<&!> baseline: a
     // mean<&!>cov tower (staggered depths) and a cov<&>cov soft join,
     // each differential .read vs .stream. The hard join additionally
     // pins CROSS-LEAF FIBER DEDUP: mean's s1-level fiber IS cov's first
     // argument — the wrapper bind must appear exactly once.
     let fusedTower = fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("zarr_strm")
let A = sd.vars.A |> z.%s
let (mu, m2) = (method_for(A) <@> lambda(x: Array<Float64 like TimeIdx>) -> prodsum(x, x) / 3.0) <&!> (method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0) |> compute
"""
                                       verb
     (match compileRun "strm_fused_read" (fusedTower "read") with
      | Error e -> printfn "  SKIP fused stream differential: .read baseline failed (%s)" e
      | Ok (refOut, _) ->
          (match compileRun "strm_fused_stream" (fusedTower "stream") with
           | Error e -> check "stream fused <&!>: streamed build runs" false e
           | Ok (out, cpp) ->
               check "stream fused <&!>: stdout identical to .read"
                   (normalize out = normalize refOut)
                   (sprintf "stream: %s / read: %s" (normalize out) (normalize refOut))
               let wrapperBinds =
                   cpp.Split('\n')
                   |> Array.filter (fun l -> l.Contains "= { A_fb_p0, A_fiber_ext }")
                   |> Array.length
               check "stream fused <&!>: shared s1 fiber bound ONCE (cross-leaf dedup)"
                   (wrapperBinds = 1) (sprintf "%d wrapper binds of A_fb_p0" wrapperBinds)))
     let fusedSoft = fun verb -> sprintf """
import zarr as z

type TimeIdx = Idx<3>
let sd = z.load("zarr_strm")
let A = sd.vars.A |> z.%s
let (m2a, m2b) = (method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) / 3.0) <&> (method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y) -> prodsum(x, y) * 2.0) |> compute
"""
                                      verb
     (match compileRun "strm_soft_read" (fusedSoft "read") with
      | Error e -> printfn "  SKIP soft-fused stream differential: .read baseline failed (%s)" e
      | Ok (refOut, _) ->
          (match compileRun "strm_soft_stream" (fusedSoft "stream") with
           | Error e -> check "stream fused <&>: streamed build runs" false e
           | Ok (out, _) ->
               check "stream fused <&>: stdout identical to .read"
                   (normalize out = normalize refOut) ""))

     // (f) elementwise consumption of a streamed source: loud reject.
     (let src = """
import zarr as z

let sd = z.load("zarr_strm")
let A = sd.vars.A |> z.stream
let out = method_for(A) <@> lambda(x) -> x + x |> compute
"""
      match lower src with
      | Ok ir ->
          (try
              CodeGen.genSelfContainedProgramFromIR ir "strm_elem_reject" |> ignore
              check "stream: elementwise consumption rejected loudly" false "codegen succeeded?"
           with ex ->
              check "stream: elementwise consumption rejected loudly"
                  (ex.Message.Contains "not stream-eligible") ex.Message)
      | Error e -> check "stream: elementwise reject case lowers" false e)
     // (e) netcdf streaming differential (needs sample.nc + libnetcdf).
     if File.Exists "sample.nc" then
         (try
             File.Copy("sample.nc", Path.Combine(e2eDir, "sample.nc"), true)
             let ncDiff (label: string) (mkSrc: string -> string) =
                 match compileRun (sprintf "strm_%s_read" label) (mkSrc "read") with
                 | Error e -> printfn "  SKIP nc stream differential: .read baseline failed (%s)" e
                 | Ok (refOut, _) ->
                     (match compileRun (sprintf "strm_%s_stream" label) (mkSrc "stream") with
                      | Error e -> check (sprintf "stream %s: streamed build runs" label) false e
                      | Ok (out, cpp) ->
                          check (sprintf "stream %s: stdout identical to .read" label)
                              (normalize out = normalize refOut) ""
                          check (sprintf "stream %s: nc_get_vara fiber reads inlined" label)
                              (cpp.Contains "nc_get_vara") "")
             ncDiff "nc_cov" (fun verb -> sprintf """
import netcdf as nc

let sample = nc.load("sample.nc")
let A = sample.vars.A |> nc.%s
let m2 = method_for(A, A) <@> lambda(x: Array<Float32 like xdim>, y: Array<Float32 like xdim>) where comm(x, y) -> prodsum(x, y) |> compute
"""
                                              verb)
          with ex -> printfn "  SKIP nc stream differential: %s" ex.Message)
     else
         printfn "  SKIP nc stream differential: sample.nc not found")

    // Cleanup the temp scratch (e2e stores stay in generated_cpp_tests
    // beside the .cpp files, like the netcdf fixtures).
    (try Directory.Delete(scratch, true) with _ -> ())

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printFooter "Zarr Provider" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    if failed > 0 then 1 else 0
