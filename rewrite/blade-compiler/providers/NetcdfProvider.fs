// Blade-DSL NetCDF Type Provider
// Compile-time metadata extraction from NetCDF files.
// Reads dimensions, variable names, types, and shapes so the compiler
// can construct IR array types.  Actual data I/O is deferred to
// generated C++ code that calls the NetCDF C API directly.

module Blade.NetcdfProvider

open System.Runtime.InteropServices
open Blade.IR

// ============================================================================
// NetCDF Metadata Types
// ============================================================================

type NcDim = {
    Name: string
    Length: int64
}

type NcVar = {
    Name: string
    Dims: NcDim list   // slowest-changing first (C/row-major order)
    TypeCode: int      // NC_* type constant
}

type NcFile = {
    Path: string
    Dims: NcDim list
    Vars: NcVar list
}

// ============================================================================
// P/Invoke Bindings to libnetcdf
// ============================================================================

module private NcFFI =

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_open(string path, int mode, int& fileId)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_close(int fileId)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_ndims(int fileId, int& ndims)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_dimids(int fileId, int& ndims, int[] dimids)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_dim(int fileId, int dimid, byte[] name, int64& length)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_nvars(int fileId, int& nvars)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_varids(int fileId, int& nvars, int[] varids)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_varname(int fileId, int varid, byte[] name)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_vartype(int fileId, int varid, int& xtype)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_varndims(int fileId, int varid, int& ndims)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_vardimid(int fileId, int varid, int[] dimids)

    let check (status: int) (msg: string) =
        if status <> 0 then failwithf "NetCDF error (%d): %s" status msg

// ============================================================================
// Safe Wrappers
// ============================================================================

module private NcQuery =

    let openFile (path: string) (mode: int) =
        let mutable id = 0
        NcFFI.nc_open(path, mode, &id) |> fun s -> NcFFI.check s (sprintf "opening '%s'" path)
        id

    let closeFile (fileId: int) =
        NcFFI.nc_close(fileId) |> fun s -> NcFFI.check s "closing file"

    let getDimIds (fileId: int) =
        let mutable ndims = 0
        NcFFI.nc_inq_ndims(fileId, &ndims) |> fun s -> NcFFI.check s "querying ndims"
        if ndims = 0 then [||]
        else
            let ids = Array.zeroCreate ndims
            let mutable n = ndims
            NcFFI.nc_inq_dimids(fileId, &n, ids) |> fun s -> NcFFI.check s "querying dimids"
            ids

    let getDim (fileId: int) (dimId: int) =
        let buf : byte[] = Array.zeroCreate 256
        let mutable length = 0L
        NcFFI.nc_inq_dim(fileId, dimId, buf, &length) |> fun s -> NcFFI.check s (sprintf "querying dim %d" dimId)
        let nul = System.Array.IndexOf(buf, 0uy)
        let len = if nul >= 0 then nul else buf.Length
        let name = System.Text.Encoding.ASCII.GetString(buf, 0, len)
        { Name = name; Length = length }

    let getVarIds (fileId: int) =
        let mutable nvars = 0
        NcFFI.nc_inq_nvars(fileId, &nvars) |> fun s -> NcFFI.check s "querying nvars"
        if nvars = 0 then [||]
        else
            let ids = Array.zeroCreate nvars
            let mutable n = nvars
            NcFFI.nc_inq_varids(fileId, &n, ids) |> fun s -> NcFFI.check s "querying varids"
            ids

    let getVar (fileId: int) (varId: int) (dimLookup: Map<int, NcDim>) =
        let nameBuf : byte[] = Array.zeroCreate 256
        NcFFI.nc_inq_varname(fileId, varId, nameBuf) |> fun s -> NcFFI.check s (sprintf "querying var %d name" varId)
        let nul = System.Array.IndexOf(nameBuf, 0uy)
        let len = if nul >= 0 then nul else nameBuf.Length
        let name = System.Text.Encoding.ASCII.GetString(nameBuf, 0, len)

        let mutable xtype = 0
        NcFFI.nc_inq_vartype(fileId, varId, &xtype) |> fun s -> NcFFI.check s (sprintf "querying var %d type" varId)

        let mutable ndims = 0
        NcFFI.nc_inq_varndims(fileId, varId, &ndims) |> fun s -> NcFFI.check s (sprintf "querying var %d ndims" varId)

        let dimIds = Array.zeroCreate ndims
        NcFFI.nc_inq_vardimid(fileId, varId, dimIds) |> fun s -> NcFFI.check s (sprintf "querying var %d dimids" varId)

        let dims =
            dimIds
            |> Array.toList
            |> List.map (fun did ->
                match Map.tryFind did dimLookup with
                | Some dim -> dim
                | None -> failwithf "Dimension ID %d not found" did)

        { Name = name; Dims = dims; TypeCode = xtype }

// ============================================================================
// File Loading (compile-time metadata extraction)
// ============================================================================

/// Load all metadata from a NetCDF file.
/// Opens the file read-only, extracts dimensions and variable info, closes.
let load (path: string) : NcFile =
    let fileId = NcQuery.openFile path 0  // NC_NOWRITE = 0
    try
        let dimIds = NcQuery.getDimIds fileId
        let dims = dimIds |> Array.map (NcQuery.getDim fileId) |> Array.toList
        let dimLookup = dimIds |> Array.mapi (fun i id -> (id, dims.[i])) |> Map.ofArray
        let varIds = NcQuery.getVarIds fileId
        let vars = varIds |> Array.map (fun vid -> NcQuery.getVar fileId vid dimLookup) |> Array.toList
        { Path = path; Dims = dims; Vars = vars }
    finally
        NcQuery.closeFile fileId

// ============================================================================
// Mapping to Blade IR Types
// ============================================================================

/// Map NetCDF type codes to the nearest Blade ElemType.
///   Integer types (byte, short, int, int64, unsigned) -> ETInt64
///   Float  -> ETFloat32
///   Double -> ETFloat64
///   Char   -> ETInt32 (treated as integer)
let ncTypeToElemType (tc: int) : ElemType =
    match tc with
    | 1  -> ETInt64     // NC_BYTE     (signed 8-bit  -> Int)
    | 2  -> ETInt32     // NC_CHAR     (8-bit char    -> Int)
    | 3  -> ETInt64     // NC_SHORT    (16-bit signed -> Int)
    | 4  -> ETInt64     // NC_INT      (32-bit signed -> Int)
    | 5  -> ETFloat32   // NC_FLOAT
    | 6  -> ETFloat64   // NC_DOUBLE
    | 7  -> ETInt64     // NC_UBYTE    (unsigned 8    -> Int)
    | 8  -> ETInt64     // NC_USHORT   (unsigned 16   -> Int)
    | 9  -> ETInt64     // NC_UINT     (unsigned 32   -> Int)
    | 10 -> ETInt64     // NC_INT64
    | 11 -> ETInt64     // NC_UINT64   (unsigned 64   -> Int)
    | _  -> failwithf "Unsupported NetCDF type code: %d" tc

/// Build a named IRIndexType from an NcDim.
/// The name becomes the nominal identity of this index space.
let ncDimToNamedIndexType (builder: IRBuilder) (dim: NcDim) : string * IRIndexType =
    let idx = {
        Id = builder.FreshId()
        Arity = 1
        Extent = IRLit (IRLitInt dim.Length)
        Symmetry = SymNone
        Tag = None
        Kind = SDimension
        Dependencies = []
    }
    (dim.Name, idx)

/// Build an IRArrayType for a variable, reusing the module's named index types.
/// dimMap provides the mapping from dimension name -> IRIndexType so that
/// variables sharing the same dimension get the same IRIndexType reference.
let ncVarToArrayType (dimMap: Map<string, IRIndexType>) (var: NcVar) : IRArrayType =
    let indexTypes =
        var.Dims
        |> List.map (fun dim ->
            match Map.tryFind dim.Name dimMap with
            | Some idx -> idx
            | None -> failwithf "Dimension '%s' not found in module" dim.Name)
    {
        ElemType = IRTScalar (ncTypeToElemType var.TypeCode)
        IndexTypes = indexTypes
        IsVirtual = false
        Identity = Some (AIDVariable var.Name)
    }

/// Convert an NcFile into an IRModule using structs for dims/vars.
///
/// Produces IR equivalent to:
///
///   module sample
///       type xdim = Idx<20>
///       type ydim = Idx<30>
///       type zdim = Idx<50>
///
///       struct dims = {
///           xdim: Array<Int64, Idx<xdim>>
///           ydim: Array<Int64, Idx<ydim>>
///           zdim: Array<Int64, Idx<zdim>>
///       }
///
///       struct vars = {
///           A: Array<Float32, Idx<zdim>, Idx<ydim>, Idx<xdim>>
///       }
///
/// Coordinate variables (1D arrays named after their dimension) go in dims.
/// All other data variables go in vars.
/// Named index types live at module scope.
///
/// Access: sample.dims.xdim (coordinate array), sample.vars.A (data array).
///
/// The dimMap parameter allows a schema to supply shared index types
/// so that multiple files get type-compatible dimensions.
let ncFileToModule
    (builder: IRBuilder)
    (moduleName: string)
    (file: NcFile)
    (externalDimMap: Map<string, IRIndexType> option)
    : IRModule =

    // Step 1: Build named index types
    let (indexTypeDefs, dimMap) =
        match externalDimMap with
        | Some dm ->
            ([], dm)
        | None ->
            let pairs = file.Dims |> List.map (ncDimToNamedIndexType builder)
            let typeDefs =
                pairs |> List.map (fun (name, idx) -> IRTDIndexType(name, idx))
            let dm = pairs |> Map.ofList
            (typeDefs, dm)

    // Step 2: dims struct — coordinate arrays (one per dimension)
    let dimsFields =
        file.Dims |> List.map (fun dim ->
            let idx = dimMap.[dim.Name]
            let arrType = IRTArray {
                ElemType = IRTScalar ETInt64
                IndexTypes = [idx]
                IsVirtual = false
                Identity = Some (AIDVariable dim.Name)
            }
            (dim.Name, arrType))

    let dimsStruct = IRTDStruct("dims", dimsFields, None)

    // Step 3: vars struct — data variables only (exclude coordinate variables)
    let dimNames = file.Dims |> List.map (fun d -> d.Name) |> Set.ofList
    let isCoordinateVar (v: NcVar) =
        dimNames.Contains v.Name
        && v.Dims.Length = 1
        && v.Dims.[0].Name = v.Name

    let varsFields =
        file.Vars
        |> List.filter (not << isCoordinateVar)
        |> List.map (fun v ->
            let arrType = ncVarToArrayType dimMap v
            (v.Name, IRTArray arrType))

    let varsStruct = IRTDStruct("vars", varsFields, None)

    {
        Name = moduleName
        Types = indexTypeDefs @ [dimsStruct; varsStruct]
        Functions = []
        Bindings = []
        StaticFunctionUsage = Map.empty
    }

/// Convenience: load a file and produce a module in one step.
let loadAsModule (builder: IRBuilder) (moduleName: string) (path: string) : IRModule =
    let file = load path
    ncFileToModule builder moduleName file None

// ============================================================================
// NetCDF C++ Code Generation Helpers
// ============================================================================
// These produce the C++ fragments that do the actual data I/O at runtime.

module CppNetcdf =

    /// Map Blade ElemType to the nc_type constant name for generated code
    let elemTypeToNcMacro = function
        | ETFloat32 -> "NC_FLOAT"
        | ETFloat64 -> "NC_DOUBLE"
        | ETInt32   -> "NC_INT"
        | ETInt64   -> "NC_INT64"
        | _         -> "NC_DOUBLE"  // fallback

    /// Generate C++ code to open a NetCDF file and read a variable
    let genReadVar (filePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        let rank = arrType.IndexTypes.Length
        // Phase B2: arrType.ElemType is IRType. NetCDF only supports
        // primitive numeric types, so extract the primitive ElemType
        // for the dispatch. Non-primitive elements are unsupported in
        // this provider — they'd require new NetCDF type machinery.
        let primElem =
            match arrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETFloat64  // S3 tag: relic. NetCDF doesn't support compound elem types yet.
        let elemCpp =
            match primElem with
            | ETFloat32 -> "float"
            | ETFloat64 -> "double"
            | ETInt32   -> "int"
            | ETInt64   -> "long long"
            | _         -> "double"

        let extentsFromDims =
            arrType.IndexTypes
            |> List.mapi (fun i idx ->
                match idx.Extent with
                | IRLit (IRLitInt n) -> sprintf "size_t %s_extent_%d = %d;" cppVarName i n
                | _ -> sprintf "size_t %s_extent_%d = /* dynamic */;" cppVarName i)

        [
            sprintf "// Read %s from %s" varName filePath
            sprintf "int %s_ncid, %s_varid;" cppVarName cppVarName
            sprintf "nc_open(\"%s\", NC_NOWRITE, &%s_ncid);" filePath cppVarName
            sprintf "nc_inq_varid(%s_ncid, \"%s\", &%s_varid);" cppVarName varName cppVarName
        ]
        @ extentsFromDims
        @ [
            sprintf "%s* %s_flat = new %s[%s];"
                elemCpp cppVarName elemCpp
                (arrType.IndexTypes
                 |> List.mapi (fun i _ -> sprintf "%s_extent_%d" cppVarName i)
                 |> String.concat " * ")
            sprintf "nc_get_var_%s(%s_ncid, %s_varid, %s_flat);"
                (match primElem with
                 | ETFloat32 -> "float"
                 | ETFloat64 -> "double"
                 | ETInt32 -> "int"
                 | ETInt64 -> "longlong"
                 | _ -> "double")
                cppVarName cppVarName cppVarName
            sprintf "nc_close(%s_ncid);" cppVarName
        ]

    /// Generate C++ code to write a variable to a NetCDF file.
    /// dimNames provides the dimension names from the module's IRTDIndexType defs.
    let genWriteVar (filePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (dimNames: string list) : string list =
        // Phase B2: same extraction as genReadVar.
        let primElem =
            match arrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETFloat64  // S3 tag: relic. NetCDF compound types unsupported.
        let elemCpp =
            match primElem with
            | ETFloat32 -> "float"
            | ETFloat64 -> "double"
            | ETInt32   -> "int"
            | ETInt64   -> "long long"
            | _         -> "double"

        let ncType = elemTypeToNcMacro primElem
        let rank = arrType.IndexTypes.Length

        let dimDefs =
            arrType.IndexTypes
            |> List.mapi (fun i idx ->
                let dimName =
                    if i < dimNames.Length then dimNames.[i]
                    else sprintf "dim%d" i
                let extent =
                    match idx.Extent with
                    | IRLit (IRLitInt n) -> sprintf "%d" n
                    | _ -> "0 /* unlimited */"
                sprintf "nc_def_dim(%s_ncid, \"%s\", %s, &%s_dimids[%d]);"
                    cppVarName dimName extent cppVarName i)

        [
            sprintf "// Write %s to %s" varName filePath
            sprintf "int %s_ncid, %s_varid;" cppVarName cppVarName
            sprintf "int %s_dimids[%d];" cppVarName rank
            sprintf "nc_create(\"%s\", NC_CLOBBER | NC_NETCDF4, &%s_ncid);" filePath cppVarName
        ]
        @ dimDefs
        @ [
            sprintf "nc_def_var(%s_ncid, \"%s\", %s, %d, %s_dimids, &%s_varid);"
                cppVarName varName ncType rank cppVarName cppVarName
            sprintf "nc_enddef(%s_ncid);" cppVarName
            sprintf "nc_put_var_%s(%s_ncid, %s_varid, %s_flat);"
                (match primElem with
                 | ETFloat32 -> "float"
                 | ETFloat64 -> "double"
                 | ETInt32 -> "int"
                 | ETInt64 -> "longlong"
                 | _ -> "double")
                cppVarName cppVarName cppVarName
            sprintf "nc_close(%s_ncid);" cppVarName
        ]

    /// Extract dimension names from a module's index type definitions
    let dimNamesFromModule (modul: IRModule) : string list =
        modul.Types
        |> List.choose (function
            | IRTDIndexType (name, _) -> Some name
            | _ -> None)

    /// Generate required C++ includes for NetCDF
    let genIncludes () : string list =
        ["#include <netcdf.h>"]
