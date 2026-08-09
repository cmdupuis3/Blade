// Blade-DSL NetCDF Type Provider: compile-time metadata extraction (dims, var names, types, shapes) for IR array types; data I/O is generated C++ calling the NetCDF C API.

module Blade.NetcdfProvider

open System.Runtime.InteropServices
open Blade.IR
open Blade.Types

// NetCDF Metadata Types

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

// P/Invoke Bindings to libnetcdf

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

    // Data reads for the compile-time fold (provider-backed statics): libnetcdf
    // converts any numeric type to the requested C type (double + longlong
    // cover the whole ncTypeToElemType surface) -- the same functions
    // CppNetcdf.genReadVar emits as source text.
    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_get_var_double(int fileId, int varid, double[] data)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_get_var_longlong(int fileId, int varid, int64[] data)

    let check (status: int) (msg: string) =
        if status <> 0 then failwithf "NetCDF error (%d): %s" status msg

// Safe Wrappers

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

// File Loading (compile-time metadata extraction)

/// Loads all metadata from a NetCDF file (opens read-only, extracts dims/vars, closes).
let load (path: string) : NcFile =
    if not (System.IO.File.Exists path) then
        failwithf "NetCDF file not found: '%s' (resolved against cwd '%s')"
            path (System.IO.Directory.GetCurrentDirectory())
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

// Compile-time DATA read (provider-backed statics)

/// A variable's payload read at compile time: dimension extents plus the
/// row-major flat buffer (host-ordered; libnetcdf handles format/chunking/endianness).
type NcVarData = {
    DimLengths: int list
    Payload: NcPayload
}
and NcPayload =
    | NcFloats of float[]
    | NcInts of int64[]

/// Reads a variable's full payload at compile time. Float-coded variables
/// arrive as doubles; every integer coding arrives as int64 (mirrors ncTypeToElemType's collapse).
let readVarData (path: string) (varName: string) : Result<NcVarData, string> =
    try
        let fileId = NcQuery.openFile path 0  // NC_NOWRITE
        try
            let dimIds = NcQuery.getDimIds fileId
            let dims = dimIds |> Array.map (NcQuery.getDim fileId) |> Array.toList
            let dimLookup = dimIds |> Array.mapi (fun i id -> (id, dims.[i])) |> Map.ofArray
            let hit =
                NcQuery.getVarIds fileId
                |> Array.tryPick (fun vid ->
                    let v = NcQuery.getVar fileId vid dimLookup
                    if v.Name = varName then Some (vid, v) else None)
            match hit with
            | None -> Error (sprintf "variable '%s' not found in '%s'" varName path)
            | Some (vid, v) ->
                let lens = v.Dims |> List.map (fun d -> int d.Length)
                let count = lens |> List.fold (*) 1
                match v.TypeCode with
                | 5 | 6 ->  // NC_FLOAT, NC_DOUBLE
                    let buf : float[] = Array.zeroCreate (max count 1)
                    NcFFI.check (NcFFI.nc_get_var_double(fileId, vid, buf)) (sprintf "reading '%s'" varName)
                    Ok { DimLengths = lens; Payload = NcFloats buf }
                | _ ->
                    let buf : int64[] = Array.zeroCreate (max count 1)
                    NcFFI.check (NcFFI.nc_get_var_longlong(fileId, vid, buf)) (sprintf "reading '%s'" varName)
                    Ok { DimLengths = lens; Payload = NcInts buf }
        finally
            NcQuery.closeFile fileId
    with ex ->
        Error ex.Message

// Mapping to Blade IR Types

/// Maps a NetCDF type code to the nearest Blade ElemType (per-code mapping below).
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

/// Builds a named IRIndexType from an NcDim; the name is this index space's nominal identity.
let ncDimToNamedIndexType (builder: IRBuilder) (dim: NcDim) : string * IRIndexType =
    let idx = {
        Id = builder.FreshId()
        Rank = 1
        Extent = IRLit (IRLitInt dim.Length)
        Symmetry = SymNone
        Tag = None; IxKind = IxKPlain
        Kind = SDimension
        Dependencies = []
    }
    (dim.Name, idx)

/// Builds an IRArrayType for a variable, reusing the module's named index
/// types (dimMap: dim name -> IRIndexType) so variables sharing a dimension get the same reference.
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

/// Converts an NcFile into an IRModule using structs for dims/vars.
/// Coordinate variables (1D arrays named after their dimension) go in
/// `dims`; all other data variables go in `vars`. Named index types live
/// at module scope. Access: sample.dims.xdim, sample.vars.A.
///
/// Produces IR equivalent to:
///
///   module sample
///       type xdim = Idx<20>
///       type ydim = Idx<30>
///       type zdim = Idx<50>
///
///       struct sample__dims = {
///           xdim: Array<Int64, Idx<xdim>>
///           ydim: Array<Int64, Idx<ydim>>
///           zdim: Array<Int64, Idx<zdim>>
///       }
///
///       struct sample__vars = {
///           A: Array<Float32, Idx<zdim>, Idx<ydim>, Idx<xdim>>
///       }
///
/// The structs are namespaced by `moduleName` rather than the bare
/// "dims"/"vars": one flat TypeDefs map means literal names would let a
/// second load silently overwrite the first; `fieldFor` resolves the
/// suffix, so `.dims`/`.vars` stay unchanged.
///
/// externalDimMap lets a schema supply shared index types across files.
let ncFileToModule
    (builder: IRBuilder)
    (moduleName: string)
    (file: NcFile)
    (externalDimMap: Map<string, IRIndexType> option)
    : IRModule =

    // Build named index types
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

    // dims struct: coordinate arrays (one per dimension)
    let dimsFields =
        file.Dims |> List.map (fun dim ->
            let idx = dimMap.[dim.Name]
            let arrType = mkArrayArrow [idx] (IRTScalar ETInt64) (Some (AIDVariable dim.Name))
            (dim.Name, arrType))

    let dimsStruct = IRTDStruct(sprintf "%s__dims" moduleName, dimsFields)

    // vars struct: data variables only (exclude coordinate variables)
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
            (v.Name, mkArrayLike arrType))

    let varsStruct = IRTDStruct(sprintf "%s__vars" moduleName, varsFields)

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
        SparseInits = Map.empty
        MutableArrayLets = Set.empty
        DerivedFuncOrigins = Map.empty
    }

/// Convenience: load a file and produce a module in one step.
let loadAsModule (builder: IRBuilder) (moduleName: string) (path: string) : IRModule =
    let file = load path
    ncFileToModule builder moduleName file None

// NetCDF C++ Code Generation Helpers: produce the C++ fragments that do
// the actual data I/O at runtime.

module CppNetcdf =

    /// Map Blade ElemType to the nc_type constant name for generated code
    let elemTypeToNcMacro = function
        | ETFloat32 -> "NC_FLOAT"
        | ETFloat64 -> "NC_DOUBLE"
        | ETInt32   -> "NC_INT"
        | ETInt64   -> "NC_INT64"
        | _         -> "NC_DOUBLE"  // fallback

    /// Wraps a fallible nc_* call: captures its status into <cppVarName>_ncstat
    /// and exits loudly on failure (an ignored status silently leaves the data
    /// buffer uninitialized). `callExpr` omits its trailing semicolon; `context`
    /// is spliced into a C++ string literal, so it must avoid double quotes.
    let private ncChecked (cppVarName: string) (context: string) (callExpr: string) : string list =
        [
            sprintf "%s_ncstat = %s;" cppVarName callExpr
            sprintf "if (%s_ncstat != NC_NOERR) { std::cerr << \"NetCDF error (%s): \" << nc_strerror(%s_ncstat) << std::endl; std::exit(1); }"
                cppVarName context cppVarName
        ]

    /// Generate C++ code to open a NetCDF file and read a variable
    let genReadVar (filePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        let rank = arrType.IndexTypes.Length
        // NetCDF only supports primitive numeric types; non-primitive
        // elements are unsupported here (would need new NetCDF machinery).
        let primElem =
            match arrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETFloat64  // NetCDF doesn't support compound elem types yet.
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

        let extentNames =
            arrType.IndexTypes |> List.mapi (fun i _ -> sprintf "%s_extent_%d" cppVarName i)
        let ncGetSuffix =
            match primElem with
            | ETFloat32 -> "float"
            | ETFloat64 -> "double"
            | ETInt32 -> "int"
            | ETInt64 -> "longlong"
            | _ -> "double"
        // Flat read: NetCDF variables are stored contiguous (row-major), so
        // read into a flat buffer first; each nc_* call is status-checked so a
        // silent failure can't hand the copy loop an uninitialized buffer.
        let flatRead =
            [
                sprintf "// Read %s from %s" varName filePath
                sprintf "int %s_ncid, %s_varid, %s_ncstat;" cppVarName cppVarName cppVarName
            ]
            @ ncChecked cppVarName (sprintf "opening '%s' to read %s" filePath varName)
                (sprintf "nc_open(\"%s\", NC_NOWRITE, &%s_ncid)" filePath cppVarName)
            @ ncChecked cppVarName (sprintf "locating variable '%s' in '%s'" varName filePath)
                (sprintf "nc_inq_varid(%s_ncid, \"%s\", &%s_varid)" cppVarName varName cppVarName)
            @ extentsFromDims
            @ [
                sprintf "%s* %s_flat = new %s[%s];"
                    elemCpp cppVarName elemCpp (String.concat " * " extentNames)
            ]
            @ ncChecked cppVarName (sprintf "reading variable '%s' from '%s'" varName filePath)
                (sprintf "nc_get_var_%s(%s_ncid, %s_varid, %s_flat)" ncGetSuffix cppVarName cppVarName cppVarName)
            @ [
                sprintf "nc_close(%s_ncid);" cppVarName
            ]
        // Materialize the nested-pointer Array indexed as <v>[i][j]...: allocate<>
        // builds the nested structure, the flat buffer is copied in (runtime-
        // bounded loops compile fast, unlike a baked literal) and released.
        // ProviderReads routes a maskless spec here, vs genReadCompoundVar.
        let idxVars = [ for i in 0 .. rank - 1 -> sprintf "%s_i%d" cppVarName i ]
        let openLoops =
            idxVars |> List.mapi (fun d iv ->
                let ind = String.replicate d "    "
                sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind iv iv extentNames.[d] iv)
        let nestedSub = idxVars |> List.map (sprintf "[%s]") |> String.concat ""
        // Row-major flat index (Horner): (((i0)*ext1 + i1)*ext2 + i2)... matches
        // NetCDF's contiguous storage order.
        let flatIdx =
            let mutable acc = idxVars.[0]
            for i in 1 .. rank - 1 do
                acc <- sprintf "(%s) * %s + %s" acc extentNames.[i] idxVars.[i]
            acc
        let bodyInd = String.replicate rank "    "
        let materialize =
            [
                sprintf "size_t %s_extents[] = { %s };" cppVarName (String.concat ", " extentNames)
                sprintf "Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };"
                    elemCpp rank cppVarName elemCpp rank cppVarName cppVarName
            ]
            @ openLoops
            @ [ sprintf "%s%s%s = %s_flat[%s];" bodyInd cppVarName nestedSub cppVarName flatIdx ]
            @ [ for d in rank - 1 .. -1 .. 0 -> sprintf "%s}" (String.replicate d "    ") ]
            @ [ sprintf "delete[] %s_flat;" cppVarName ]
        flatRead @ materialize

    /// Reads a variable as a COMPOUND (masked) array, triggered only by
    /// load_compound: `maskName` is any integer array (nonzero = present);
    /// the dense var is scattered into a compact buffer of cardinality ==
    /// popcount(mask). All RANK dims are compound, so the result is a scalar
    /// nested_array_utilities::Compound<T, RANK> (heap-allocated, non-owning
    /// per the allocate<> convention).
    ///
    /// Scatter ordering: compound_index_t::enumerate walks tuples row-major,
    /// assigning rank = row-major prefix popcount, matching nc_get_var's own
    /// row-major reads -- so one sequential copy over set cells reproduces
    /// the compact layout exactly, no per-cell linearize() needed.
    let genReadCompoundVar
            (filePath: string) (varName: string) (maskName: string)
            (cppVarName: string) (varArrType: IRArrayType) (maskArrType: IRArrayType) : string list =
        // The mask covers the leading (compound) dims; remaining variable dims
        // are regular trailing dims folded into a runtime trailing_stride.
        let leadRank = maskArrType.IndexTypes.Length
        let primElem =
            match varArrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETFloat64
        let elemCpp =
            match primElem with
            | ETFloat32 -> "float"
            | ETFloat64 -> "double"
            | ETInt32   -> "int"
            | ETInt64   -> "long long"
            | _         -> "double"
        let maskElem =
            match maskArrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETInt64
        let maskCpp =
            match maskElem with
            | ETInt32 -> "int"
            | ETInt64 -> "long long"
            | ETBool  -> "signed char"
            | _       -> "long long"
        // nc_get_var_<suffix> for an ElemType (mask reads via schar when bool).
        let ncGet et =
            match et with
            | ETFloat32 -> "float"
            | ETFloat64 -> "double"
            | ETInt32   -> "int"
            | ETInt64   -> "longlong"
            | ETBool    -> "schar"
            | _         -> "double"

        let extentsFromDims =
            varArrType.IndexTypes
            |> List.mapi (fun i idx ->
                match idx.Extent with
                | IRLit (IRLitInt n) -> sprintf "size_t %s_extent_%d = %d;" cppVarName i n
                | _ -> sprintf "size_t %s_extent_%d = /* dynamic */;" cppVarName i)
        let extentNames =
            varArrType.IndexTypes |> List.mapi (fun i _ -> sprintf "%s_extent_%d" cppVarName i)
        let v = cppVarName
        let leadExtentNames = extentNames |> List.truncate leadRank
        let trailExtentNames = extentNames |> List.skip leadRank
        let gridExpr = leadExtentNames |> String.concat " * "
        let trailExpr = match trailExtentNames with | [] -> "1" | xs -> String.concat " * " xs
        let totalExpr = extentNames |> String.concat " * "
        let leadExtentsInit = leadExtentNames |> String.concat ", "

        [
            sprintf "// Read compound %s (masked by %s) from %s" varName maskName filePath
            sprintf "int %s_ncid, %s_varid, %s_maskid, %s_ncstat;" v v v v
        ]
        @ ncChecked v (sprintf "opening '%s' to read %s" filePath varName)
            (sprintf "nc_open(\"%s\", NC_NOWRITE, &%s_ncid)" filePath v)
        @ extentsFromDims
        @ [
            // grid = masked leading cells; trail = regular trailing stride; total = dense size
            sprintf "size_t %s_grid = %s;" v gridExpr
            sprintf "size_t %s_trail = %s;" v trailExpr
            sprintf "size_t %s_total = %s;" v totalExpr
            // dense variable (all dims)
            sprintf "%s* %s_dense = new %s[%s_total];" elemCpp v elemCpp v
        ]
        @ ncChecked v (sprintf "locating variable '%s' in '%s'" varName filePath)
            (sprintf "nc_inq_varid(%s_ncid, \"%s\", &%s_varid)" v varName v)
        @ ncChecked v (sprintf "reading variable '%s' from '%s'" varName filePath)
            (sprintf "nc_get_var_%s(%s_ncid, %s_varid, %s_dense)" (ncGet primElem) v v v)
        @ [
            // integer mask over the leading masked dims -- size is grid, not total
            sprintf "%s* %s_maskraw = new %s[%s_grid];" maskCpp v maskCpp v
        ]
        @ ncChecked v (sprintf "locating mask '%s' in '%s'" maskName filePath)
            (sprintf "nc_inq_varid(%s_ncid, \"%s\", &%s_maskid)" v maskName v)
        @ ncChecked v (sprintf "reading mask '%s' from '%s'" maskName filePath)
            (sprintf "nc_get_var_%s(%s_ncid, %s_maskid, %s_maskraw)" (ncGet maskElem) v v v)
        @ [
            sprintf "nc_close(%s_ncid);" v
            // int -> std::vector<bool> (nonzero = present): the load_compound conversion
            sprintf "std::vector<bool> %s_maskvec(%s_grid);" v v
            sprintf "for (size_t %s_i = 0; %s_i < %s_grid; %s_i++) %s_maskvec[%s_i] = (%s_maskraw[%s_i] != 0);" v v v v v v v v
            sprintf "delete[] %s_maskraw;" v
            // compound index over the leading masked dims
            sprintf "std::array<size_t, %d> %s_extents = { %s };" leadRank v leadExtentsInit
            sprintf "compound_index_t<%d>* %s_idx = new compound_index_t<%d>(\"%s\", %s_extents, %s_maskvec);" leadRank v leadRank varName v v
            // compact backing: present leading cells x trailing block
            sprintf "%s* %s_compact = new %s[%s_idx->cardinality * %s_trail];" elemCpp v elemCpp v v
            // scatter: copy each present cell's trailing block (row-major
            // prefix-popcount order); string-concatenated so the count can't drift.
            ("{ size_t " + v + "_r = 0; for (size_t " + v + "_c = 0; " + v + "_c < " + v + "_grid; " + v + "_c++) if (" + v + "_maskvec[" + v + "_c]) { for (size_t " + v + "_t = 0; " + v + "_t < " + v + "_trail; " + v + "_t++) " + v + "_compact[" + v + "_r * " + v + "_trail + " + v + "_t] = " + v + "_dense[" + v + "_c * " + v + "_trail + " + v + "_t]; " + v + "_r++; } }")
            sprintf "delete[] %s_dense;" v
            // bundle into the non-owning Compound wrapper (trailing_stride = _trail; 1 when all dims are masked)
            sprintf "nested_array_utilities::Compound<%s, %d> %s { %s_compact, %s_idx, %s_trail };" elemCpp leadRank v v v v
        ]

    /// Generates C++ to write a variable to a NetCDF file. dimNames provides
    /// the dimension names from the module's IRTDIndexType defs.
    let genWriteVar (filePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (dimNames: string list) : string list =
        // Same primitive-elem extraction as genReadVar.
        let primElem =
            match arrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETFloat64  // NetCDF compound types unsupported.
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
                ncChecked cppVarName (sprintf "defining dimension '%s' in '%s'" dimName filePath)
                    (sprintf "nc_def_dim(%s_ncid, \"%s\", %s, &%s_dimids[%d])"
                        cppVarName dimName extent cppVarName i))
            |> List.concat

        [
            sprintf "// Write %s to %s" varName filePath
            sprintf "int %s_ncid, %s_varid, %s_ncstat;" cppVarName cppVarName cppVarName
            sprintf "int %s_dimids[%d];" cppVarName rank
        ]
        @ ncChecked cppVarName (sprintf "creating '%s' to write %s" filePath varName)
            (sprintf "nc_create(\"%s\", NC_CLOBBER | NC_NETCDF4, &%s_ncid)" filePath cppVarName)
        @ dimDefs
        @ ncChecked cppVarName (sprintf "defining variable '%s' in '%s'" varName filePath)
            (sprintf "nc_def_var(%s_ncid, \"%s\", %s, %d, %s_dimids, &%s_varid)"
                cppVarName varName ncType rank cppVarName cppVarName)
        @ ncChecked cppVarName (sprintf "ending define mode for '%s'" filePath)
            (sprintf "nc_enddef(%s_ncid)" cppVarName)
        @ ncChecked cppVarName (sprintf "writing variable '%s' to '%s'" varName filePath)
            (sprintf "nc_put_var_%s(%s_ncid, %s_varid, %s_flat)"
                (match primElem with
                 | ETFloat32 -> "float"
                 | ETFloat64 -> "double"
                 | ETInt32 -> "int"
                 | ETInt64 -> "longlong"
                 | _ -> "double")
                cppVarName cppVarName cppVarName)
        // nc_close flushes buffered writes, so its status matters here (unlike the read paths).
        @ ncChecked cppVarName (sprintf "closing '%s' after writing %s" filePath varName)
            (sprintf "nc_close(%s_ncid)" cppVarName)

    /// STREAMED fiber reads: hoisted prologue -- opens the file once and
    /// declares the start/count vectors for nc_get_vara; the handle stays open for the program's lifetime.
    let genStreamOpen (filePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        let v = cppVarName
        let primElem =
            match arrType.ElemType with
            | IRTScalar et -> et
            | _ -> ETFloat64
        let elemCpp =
            match primElem with
            | ETFloat32 -> "float"
            | ETFloat64 -> "double"
            | ETInt32   -> "int"
            | ETInt64   -> "long long"
            | _         -> "double"
        let rank = arrType.IndexTypes.Length
        if rank < 2 then
            failwithf "NetCDF stream of '%s': needs at least one site dim plus the trailing fiber axis (rank >= 2)" varName
        if arrType.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone || ix.Rank <> 1) then
            failwithf "NetCDF stream of '%s': dense variables only" varName
        let extents =
            arrType.IndexTypes |> List.map (fun ix ->
                match ix.Extent with
                | IRLit (IRLitInt n) -> n
                | _ -> failwithf "NetCDF stream of '%s' requires literal extents" varName)
        let fiberLen = List.last extents
        ignore elemCpp
        [ sprintf "// Stream %s from %s (fiber reads inlined at the S/T boundary)" varName filePath
          sprintf "int %s_ncid, %s_varid, %s_ncstat;" v v v ]
        @ ncChecked v (sprintf "opening '%s' to stream %s" filePath varName)
            (sprintf "nc_open(\"%s\", NC_NOWRITE, &%s_ncid)" filePath v)
        @ ncChecked v (sprintf "locating variable '%s' in '%s'" varName filePath)
            (sprintf "nc_inq_varid(%s_ncid, \"%s\", &%s_varid)" v varName v)
        @ [ sprintf "size_t %s_fiber_ext[1] = { %d };" v fiberLen
            sprintf "size_t %s_start[%d]; size_t %s_count[%d];" v rank v rank ]
        @ [ for d in 0 .. rank - 2 -> sprintf "%s_count[%d] = 1;" v d ]
        @ [ sprintf "%s_count[%d] = %d;" v (rank - 1) fiberLen
            sprintf "%s_start[%d] = 0;" v (rank - 1) ]

    /// STREAMED fiber reads: the in-nest read -- sets the site coordinates and pulls one trailing-axis fiber into the destination buffer.
    let genStreamFiber (filePath: string) (varName: string) (cppVarName: string) (destBuf: string) (siteExprs: string list) (arrType: IRArrayType) : string list =
        let v = cppVarName
        let suffix =
            match arrType.ElemType with
            | IRTScalar ETFloat32 -> "float"
            | IRTScalar ETFloat64 -> "double"
            | IRTScalar ETInt32 -> "int"
            | IRTScalar ETInt64 -> "longlong"
            | _ -> "double"
        [ for d in 0 .. siteExprs.Length - 1 -> sprintf "%s_start[%d] = (size_t)(%s);" v d siteExprs.[d] ]
        @ ncChecked v (sprintf "streaming a fiber of '%s' from '%s'" varName filePath)
            (sprintf "nc_get_vara_%s(%s_ncid, %s_varid, %s_start, %s_count, %s)" suffix v v v v destBuf)

    /// Extract dimension names from a module's index type definitions
    let dimNamesFromModule (modul: IRModule) : string list =
        modul.Types
        |> List.choose (function
            | IRTDIndexType (name, _) -> Some name
            | _ -> None)

    /// Generate required C++ includes for NetCDF
    let genIncludes () : string list =
        ["#include <netcdf.h>"]
