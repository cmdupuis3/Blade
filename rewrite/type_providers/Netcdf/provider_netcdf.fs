
open System.Runtime.InteropServices

type ncDim = {
    Name: string
    Length: int64
}

type ncVar = {
    Name: string
    Dims: ncDim list // sorted from slowest to fastest changing
    TypeCode: int
}

type NetcdfFile = {
    Name: string
    Dims: ncDim list
    Vars: ncVar list
}


module Netcdf =

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_open(string path, int mode, int& fileId)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_close(int fileId)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_ndims(int fileId, int& ndims)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_dimids(int fileId, int& ndims, int[] dimids)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_dim(int fileId, int dimid, char[] name, int64& length)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_nvars(int fileId, int& nvars)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_varids(int fileId, int& nvars, int[] varids)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_varname(int fileId, int varid, char[] name)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_vartype(int fileId, int varid, int& xtype)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_varndims(int fileId, int varid, int& ndims)

    [<DllImport("netcdf", CallingConvention = CallingConvention.Cdecl)>]
    extern int nc_inq_vardimid(int fileId, int varid, int[] dimids)

    let private checkError (status: int) (msg: string) =
        if status <> 0 then failwithf "%s: %d" msg status

    let checkOpen (path: string) (mode: int) =
        let mutable id = 0
        let status = nc_open(path, mode, &id)
        checkError status "Failed to open netCDF file"
        id

    let checkClose (fileId: int) =
        let status = nc_close(fileId)
        checkError status "Failed to close netCDF file"

    let checkInqNdims (fileId: int) =
        let mutable ndims = 0
        let status = nc_inq_ndims(fileId, &ndims)
        checkError status "Failed to query number of dimensions"
        ndims

    let checkInqDimids (fileId: int) (ndims: int) =
        let dimids = Array.zeroCreate ndims
        let mutable n = ndims
        let status = nc_inq_dimids(fileId, &n, dimids)
        checkError status "Failed to query dimension IDs"
        dimids

    let checkInqDim (fileId: int) (dimId: int) =
        let nameBuffer = Array.zeroCreate 256
        let mutable length = 0L
        let status = nc_inq_dim(fileId, dimId, nameBuffer, &length)
        checkError status "Failed to query dimension info"
        (nameBuffer, length)

    let checkInqNvars (fileId: int) =
        let mutable nvars = 0
        let status = nc_inq_nvars(fileId, &nvars)
        checkError status "Failed to query number of variables"
        nvars

    let checkInqVarids (fileId: int) (nvars: int) =
        let varids = Array.zeroCreate nvars
        let mutable n = nvars
        let status = nc_inq_varids(fileId, &n, varids)
        checkError status "Failed to query variable IDs"
        varids

    let checkInqVarname (fileId: int) (varid: int) =
        let nameBuffer = Array.zeroCreate 256
        let status = nc_inq_varname(fileId, varid, nameBuffer)
        checkError status "Failed to query variable name"
        nameBuffer

    let checkInqVartype (fileId: int) (varid: int) =
        let mutable xtype = 0
        let status = nc_inq_vartype(fileId, varid, &xtype)
        checkError status "Failed to query variable type"
        xtype

    let checkInqVarndims (fileId: int) (varid: int) =
        let mutable ndims = 0
        let status = nc_inq_varndims(fileId, varid, &ndims)
        checkError status "Failed to query variable ndims"
        ndims

    let checkInqVardimid (fileId: int) (varid: int) (ndims: int) =
        let dimids = Array.zeroCreate ndims
        let status = nc_inq_vardimid(fileId, varid, dimids)
        checkError status "Failed to query variable dimids"
        dimids


module NetcdfProvider =

    let openFile (path: string) (mode: int Option) =
        let openMode = defaultArg mode 0
        Netcdf.checkOpen path openMode

    let closeFile (fileId: int) =
        Netcdf.checkClose fileId

    let getDimensionIds (fileId: int) : int list =
        let ndims = Netcdf.checkInqNdims fileId
        if ndims = 0 then []
        else Netcdf.checkInqDimids fileId ndims |> Array.toList

    let dimIdsToNcDims (fileId: int) (dimIds: int list) : ncDim list =
        dimIds
        |> List.map (fun dimId ->
            let (nameBuffer, length) = Netcdf.checkInqDim fileId dimId
            let name = System.String(nameBuffer) |> fun s -> s.TrimEnd('\000')
            { Name = name; Length = length }
        )

    let getVariableIds (fileId: int) : int list =
        let nvars = Netcdf.checkInqNvars fileId
        if nvars = 0 then []
        else Netcdf.checkInqVarids fileId nvars |> Array.toList

    let varIdsToNcVars (fileId: int) (varIds: int list) (fileDims: ncDim list) : ncVar list =
        let dimIdToNcDim = 
            fileDims
            |> List.mapi (fun i dim -> (i, dim))
            |> Map.ofList
        
        varIds
        |> List.map (fun varid ->
            let nameBuffer = Netcdf.checkInqVarname fileId varid
            let name = System.String(nameBuffer) |> fun s -> s.TrimEnd('\000')
            let xtype = Netcdf.checkInqVartype fileId varid
            let ndims = Netcdf.checkInqVarndims fileId varid
            let dimidsBuffer = Netcdf.checkInqVardimid fileId varid ndims
            
            let dims = 
                dimidsBuffer
                |> Array.toList
                |> List.map (fun dimId -> 
                    match Map.tryFind dimId dimIdToNcDim with
                    | Some dim -> dim
                    | None -> failwithf "Dimension ID %d not found in file dimensions" dimId
                )
            
            { Name = name; Dims = dims; TypeCode = xtype }
        )

    let load (path: string) : NetcdfFile =
        let fileId = openFile path None
        try
            let dims = dimIdsToNcDims fileId (getDimensionIds fileId)
            let vars = varIdsToNcVars fileId (getVariableIds fileId) dims
            { Name = path; Dims = dims; Vars = vars }
        finally
            closeFile fileId



// Needs more review
module NetcdfIR =
    
    let typeCodeToElementType (tc: int) : IRElementType =
        match tc with
        | 1 -> ETInt8      // NC_BYTE
        | 2 -> ETChar      // NC_CHAR
        | 3 -> ETInt16     // NC_SHORT
        | 4 -> ETInt32     // NC_INT
        | 5 -> ETFloat32   // NC_FLOAT
        | 6 -> ETFloat64   // NC_DOUBLE
        | 7 -> ETUInt8     // NC_UBYTE
        | 8 -> ETUInt16    // NC_USHORT
        | 9 -> ETUInt32    // NC_UINT
        | 10 -> ETInt64    // NC_INT64
        | 11 -> ETUInt64   // NC_UINT64
        | _ -> failwithf "Unsupported NetCDF type: %d" tc
    
    let ncDimToIndexType (dim: ncDim) : IRIndexType =
        {
            Tag = Some dim.Name
            Extent = IRLit (IRLitInt dim.Length)
            Symmetry = SymNone
            Kind = SDimension
            Arity = 1
        }
    
    let ncVarToArrayType (var: ncVar) : IRArrayType =
        {
            ElementType = typeCodeToElementType var.TypeCode
            IndexTypes = var.Dims |> List.map ncDimToIndexType
        }
    
    let ncFileToIR (file: NetcdfFile) : (string * IRArrayType) list =
        file.Vars 
        |> List.map (fun v -> (v.Name, ncVarToArrayType v))
