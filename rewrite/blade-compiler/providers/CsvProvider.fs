// Blade-DSL CSV Provider
// Comma-separated text tables as compile-time-shaped array I/O.
//
// A CSV file is a single text file; metadata (shape, dtype) comes from
// parsing it at compile time — the same parse also serves the static fold
// and the interpreter (ReadVarData). Runtime data I/O is deferred to
// generated C++ using only <fstream>/<sstream> (no link-time dependency,
// like ZarrProvider; contrast NetcdfProvider's libnetcdf).
//
// File model (sniffed off the first non-empty record, deterministically —
// the SAME rule the C++ reader bakes in):
//   - Every first-row cell numeric  -> MATRIX mode: R x C numbers, one 2-D
//     var named `data` with plain (anonymous) Idx axes.
//   - Otherwise                     -> HEADERED mode: first row = column
//     labels; one 2-D var named `data` whose COLUMN axis is a synthesized
//     EnumIdx over the labels (`<binding>_cols`), so columns are selected
//     by label: `obs.vars.data[i, "temp"]`. Labels are arbitrary non-empty
//     unique strings (EnumIdx values, not identifiers). A header whose
//     labels ALL look numeric is indistinguishable from a matrix and is
//     documented as unsupported (rename a column).
//
// Format rules (v1, enforced identically here and in the emitted C++):
//   - Delimiter is comma only. No quoting/escaping — any '"' is an error.
//   - LF and CRLF both accepted (one trailing '\r' stripped per line);
//     a UTF-8 BOM on line 1 is stripped; one trailing newline tolerated.
//   - Ragged rows, empty cells, and interior blank lines are errors with
//     line numbers.
//   - Data cells are numeric only (strings are deferred — ProviderPayload
//     is closed over floats/ints). Whole-table dtype: every cell an
//     integer literal -> Int64, else Float64 (locale-independent parsing;
//     "nan"/"inf"/"-inf" accepted as float specials to round-trip C++
//     output).
//
// Writes (`c.write("out.csv", A)`, rank <= 2): no header row; rank-1
// writes one value per line (re-loads as R x 1), rank-2 writes comma rows.
// Floats print with 17 significant digits AND a forced decimal point so
// `2.0` never re-loads as Int64.
module Blade.CsvProvider

open System
open System.IO
open Blade.IR
open Blade.Types

// ============================================================================
// Metadata model
// ============================================================================

type CsvShape =
    /// First row = column labels; Rows = data-row count (header excluded).
    | CsvTable of labels: string list * rows: int
    /// Headerless all-numeric grid.
    | CsvMatrix of rows: int * cols: int

type CsvFile = {
    Path: string
    Shape: CsvShape
    /// Whole-table element type: ETInt64 iff EVERY data cell is an integer
    /// literal, ETFloat64 otherwise.
    Elem: ElemType
}

/// Column count regardless of mode.
let colCount (f: CsvFile) =
    match f.Shape with
    | CsvTable (labels, _) -> labels.Length
    | CsvMatrix (_, c) -> c

let rowCount (f: CsvFile) =
    match f.Shape with
    | CsvTable (_, r) -> r
    | CsvMatrix (r, _) -> r

// ============================================================================
// The one parser (metadata, fold payload, and interp reads all derive)
// ============================================================================

/// Integer-literal cell: optional sign, digits only. This decides Int64 vs
/// Float64 — "1e5" and "1.0" are floats even though integral in value.
let private isIntCell (s: string) =
    let s = s.Trim()
    if s.Length = 0 then false
    else
        let body = if s.[0] = '+' || s.[0] = '-' then s.Substring 1 else s
        body.Length > 0 && body |> Seq.forall Char.IsDigit

/// Locale-independent float parse; accepts the C-locale specials the C++
/// writer can produce ("nan", "inf", "-inf") which .NET's parser spells
/// differently ("NaN", "Infinity").
let private tryParseFloat (s: string) : float option =
    let t = s.Trim()
    match t.ToLowerInvariant() with
    | "nan" | "+nan" | "-nan" -> Some nan
    | "inf" | "+inf" | "infinity" | "+infinity" -> Some infinity
    | "-inf" | "-infinity" -> Some (-infinity)
    | _ ->
        match Double.TryParse(t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

let private isNumericCell (s: string) = (tryParseFloat s).IsSome

/// Raw rectangular cells. Validates the v1 format rules; every error names
/// the 1-based line. Lines arrive newline-split with '\r' stripped and BOM
/// removed; a single trailing empty line (the trailing-newline artifact) is
/// dropped before validation.
let parseCells (path: string) (text: string) : Result<string[][], string> =
    // NB: the char literal below is U+FEFF (the BOM itself) — invisible in
    // most editors.
    let text = if text.Length > 0 && text.[0] = '﻿' then text.Substring 1 else text
    let rawLines = text.Split '\n' |> Array.map (fun l -> if l.EndsWith "\r" then l.Substring(0, l.Length - 1) else l)
    // One trailing newline => one trailing "" entry; tolerate exactly that.
    let lines =
        if rawLines.Length > 0 && rawLines.[rawLines.Length - 1] = "" then
            rawLines.[.. rawLines.Length - 2]
        else rawLines
    if lines.Length = 0 then Error (sprintf "'%s' is empty" path)
    else
        let mutable err = None
        let cells =
            lines |> Array.mapi (fun i line ->
                let lineNo = i + 1
                if err.IsSome then [||]
                elif line = "" then
                    err <- Some (sprintf "blank line in '%s' at line %d" path lineNo); [||]
                elif line.Contains "\"" then
                    err <- Some (sprintf "quote character in '%s' at line %d — quoting/escaping is not supported (v1)" path lineNo); [||]
                else
                    let row = line.Split ','
                    match row |> Array.tryFindIndex (fun c -> c.Trim() = "") with
                    | Some ci ->
                        err <- Some (sprintf "empty cell (column %d) in '%s' at line %d" (ci + 1) path lineNo); [||]
                    | None -> row |> Array.map (fun c -> c.Trim()))
        match err with
        | Some e -> Error e
        | None ->
            let width = cells.[0].Length
            match cells |> Array.tryFindIndex (fun r -> r.Length <> width) with
            | Some i ->
                Error (sprintf "ragged row in '%s' at line %d: %d cells where line 1 has %d" path (i + 1) cells.[i].Length width)
            | None -> Ok cells

/// Parse + classify: the sniffing rule, label validation, dtype inference.
/// Returns the metadata and the full cell grid (data rows only start at
/// row 1 for tables).
let parseFile (path: string) : Result<CsvFile * string[][], string> =
    if not (File.Exists path) then
        Error (sprintf "CSV file not found: '%s' (resolved against cwd '%s')" path (Directory.GetCurrentDirectory()))
    else
    parseCells path (File.ReadAllText path) |> Result.bind (fun cells ->
        let headered = not (cells.[0] |> Array.forall isNumericCell)
        let dataRows = if headered then cells.[1..] else cells
        if dataRows.Length = 0 then
            Error (sprintf "'%s' has a header row but no data rows" path)
        else
            // Validate every data cell numeric; name the first offender.
            let mutable bad = None
            for i in 0 .. dataRows.Length - 1 do
                if bad.IsNone then
                    match dataRows.[i] |> Array.tryFindIndex (not << isNumericCell) with
                    | Some ci ->
                        let lineNo = (if headered then i + 2 else i + 1)
                        bad <- Some (sprintf "non-numeric cell '%s' (column %d) in '%s' at line %d — string columns are not supported (v1)" dataRows.[i].[ci] (ci + 1) path lineNo)
                    | None -> ()
            match bad with
            | Some e -> Error e
            | None ->
                let shapeResult =
                    if headered then
                        let labels = cells.[0] |> List.ofArray
                        let dup =
                            labels |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1)
                        match dup with
                        | Some (l, _) -> Error (sprintf "duplicate column label '%s' in '%s'" l path)
                        | None -> Ok (CsvTable (labels, dataRows.Length))
                    else
                        Ok (CsvMatrix (dataRows.Length, cells.[0].Length))
                shapeResult |> Result.map (fun shape ->
                    let elem =
                        if dataRows |> Array.forall (Array.forall isIntCell) then ETInt64 else ETFloat64
                    ({ Path = path; Shape = shape; Elem = elem }, cells))
    )

/// Metadata-only load; throws with path + cwd detail on any failure (the
/// TypeCheck call site swallows exceptions silently — Lowering's uncaught
/// call is the loud surface, so the message must carry everything).
let loadMeta (path: string) : CsvFile =
    match parseFile path with
    | Ok (f, _) -> f
    | Error e -> failwithf "CSV load failed: %s" e

// ============================================================================
// Compile-time payload (static fold + interpreter dense reads)
// ============================================================================

/// The single 2-D var every CSV module exposes.
[<Literal>]
let DataVarName = "data"

let readVarData (path: string) (varName: string) : Result<Blade.ProviderRegistry.ProviderVarData, string> =
    if varName <> DataVarName then
        Error (sprintf "CSV file '%s' has no variable '%s' (the only variable is '%s')" path varName DataVarName)
    else
        parseFile path |> Result.bind (fun (f, cells) ->
            let headered = match f.Shape with CsvTable _ -> true | CsvMatrix _ -> false
            let dataRows = if headered then cells.[1..] else cells
            let rows = dataRows.Length
            let cols = colCount f
            let payload =
                match f.Elem with
                | ETInt64 ->
                    let xs = Array.zeroCreate<int64> (rows * cols)
                    for r in 0 .. rows - 1 do
                        for c in 0 .. cols - 1 do
                            xs.[r * cols + c] <- Int64.Parse(dataRows.[r].[c], Globalization.CultureInfo.InvariantCulture)
                    Blade.ProviderRegistry.PInts xs
                | _ ->
                    let xs = Array.zeroCreate<float> (rows * cols)
                    for r in 0 .. rows - 1 do
                        for c in 0 .. cols - 1 do
                            xs.[r * cols + c] <-
                                match tryParseFloat dataRows.[r].[c] with
                                | Some v -> v
                                | None -> failwith "unreachable: cells validated numeric by parseFile"
                    Blade.ProviderRegistry.PFloats xs
            Ok { DimLengths = [rows; cols]; Payload = payload })

// ============================================================================
// Mapping to Blade IR types
// ============================================================================

/// Anonymous plain index (matrix axes and the table's row axis).
let private anonIdx (builder: IRBuilder) (extent: int64) : IRIndexType =
    { Id = builder.FreshId()
      Rank = 1
      Extent = IRLit (IRLitInt extent)
      Symmetry = SymNone
      Tag = None; IxKind = IxKPlain
      Kind = SDimension
      Dependencies = [] }

/// The synthesized column-EnumIdx tag for a headered load bound as `name`.
let colsTagName (moduleName: string) = sprintf "%s_cols" moduleName

/// Compile-time metadata -> IRModule. One var `data` in a `<name>__vars`
/// struct (uniquely named so several CSV loads in one program don't clobber
/// each other in the TypeDefs map — registerProviderModule resolves the
/// suffixed names). Headered mode additionally emits an IRTDEnumIdx for the
/// column axis; registerProviderModule registers it so string-literal
/// column subscripts fold to ordinals at the indexing site.
let loadAsModule (builder: IRBuilder) (moduleName: string) (path: string) : IRModule =
    let f = loadMeta path
    let rows = int64 (match f.Shape with CsvTable (_, r) -> r | CsvMatrix (r, _) -> r)
    let cols = int64 (colCount f)
    let rowIdx = anonIdx builder rows
    let (colIdx, enumDefs) =
        match f.Shape with
        | CsvMatrix _ -> (anonIdx builder cols, [])
        | CsvTable (labels, _) ->
            let tag = colsTagName moduleName
            let idx =
                { Id = builder.FreshId()
                  Rank = 1
                  Extent = IRLit (IRLitInt cols)
                  Symmetry = SymNone
                  Tag = Some tag; IxKind = ixKindOfTag (Some tag)
                  Kind = SDimension
                  Dependencies = [] }
            let values = labels |> List.map EVString
            (idx, [IRTDEnumIdx (tag, idx, values)])
    let arrType = {
        ElemType = IRTScalar f.Elem
        IndexTypes = [rowIdx; colIdx]
        IsVirtual = false
        Identity = Some (AIDVariable DataVarName)
    }
    let varsStruct = IRTDStruct (sprintf "%s__vars" moduleName, [(DataVarName, mkArrayLike arrType)])
    {
        Name = moduleName
        Types = enumDefs @ [varsStruct]
        Functions = []
        Bindings = []
        StaticFunctionUsage = Map.empty
        ProviderReads = Map.empty
        ProviderWrites = Map.empty
        RandomInits = Map.empty
        CompoundInits = Map.empty
        MutableArrayLets = Set.empty
    }

// ============================================================================
// Fingerprint / version stamp (single-file provenance)
// ============================================================================

let fileFingerprint (path: string) : string =
    use sha = Security.Cryptography.SHA256.Create()
    sha.ComputeHash(File.ReadAllBytes path)
    |> Array.map (sprintf "%02x")
    |> String.concat ""

let fileVersionStamp (path: string) : int64 =
    try File.GetLastWriteTimeUtc(path).Ticks with _ -> 0L

// ============================================================================
// C++ code generation (pure std C++17: <fstream>, <sstream>)
// ============================================================================

module CppCsv =

    let private elemCppOf (t: IRType) : string =
        match t with
        | IRTScalar ETInt64 -> "long long"
        | IRTScalar ETInt32 -> "int"
        | IRTScalar ETFloat32 -> "float"
        | _ -> "double"

    /// C++ string literal for a path (forward slashes; no escaping beyond
    /// backslash normalization — paths come from Blade string literals).
    let private cppPath (p: string) = p.Replace("\\", "/")

    let private csvExit (v: string) (msg: string) =
        sprintf "{ std::cerr << \"CSV error: %s\" << std::endl; std::exit(1); }" msg

    /// Emit the parse-and-fill block: opens the file, re-applies the v1
    /// format rules (BOM/CRLF/quotes/ragged/blank), validates the baked
    /// shape, and fills `<v>_flat` (row-major R x C). Locale note: strtod/
    /// strtoll are locale-sensitive in principle, but generated programs
    /// never call setlocale, so the "C" locale is guaranteed — keep it that
    /// way.
    let private genParseFill (path: string) (v: string) (elemCpp: string) (isInt: bool)
                             (headered: bool) (rows: int64) (cols: int64) : string list =
        let p = cppPath path
        let convert =
            if isInt then
                [ sprintf "            long long %s_val = std::strtoll(%s_cs, &%s_end, 10);" v v v ]
            else
                [ sprintf "            double %s_val = std::strtod(%s_cs, &%s_end);" v v v ]
        [ sprintf "// Read %s from CSV %s (%d x %d%s)" v p rows cols (if headered then ", headered" else "")
          sprintf "%s* %s_flat = new %s[%d];" elemCpp v elemCpp (rows * cols)
          "{"
          sprintf "    std::ifstream %s_in(\"%s\");" v p
          sprintf "    if (!%s_in) %s" v (csvExit v (sprintf "cannot open '%s'" p))
          sprintf "    std::string %s_line;" v
          sprintf "    size_t %s_row = 0, %s_lineno = 0;" v v
          sprintf "    while (std::getline(%s_in, %s_line)) {" v v
          sprintf "        %s_lineno++;" v
          sprintf "        if (!%s_line.empty() && %s_line.back() == '\\r') %s_line.pop_back();" v v v
          sprintf "        if (%s_lineno == 1 && %s_line.size() >= 3 && (unsigned char)%s_line[0] == 0xEF && (unsigned char)%s_line[1] == 0xBB && (unsigned char)%s_line[2] == 0xBF) %s_line.erase(0, 3);" v v v v v v
          // A blank line is legal only as the very last line (trailing-
          // newline artifact); getline itself absorbs ONE trailing newline,
          // so a blank here means "\n\n" at EOF or an interior blank.
          sprintf "        if (%s_line.empty()) { if (%s_in.peek() == EOF) break; %s }" v v
              (csvExit v (sprintf "blank line in '%s' at line \" << %s_lineno << \"" p v))
          sprintf "        if (%s_line.find('\"') != std::string::npos) %s" v
              (csvExit v (sprintf "quote character in '%s' at line \" << %s_lineno << \" — quoting is not supported (v1)" p v)) ]
        @ (if headered then
            [ sprintf "        if (%s_lineno == 1) continue;  // header row (labels baked at compile time)" v ]
           else [])
        @ [ sprintf "        if (%s_row >= %d) %s" v rows
                (csvExit v (sprintf "'%s' has more data rows than the %d baked at compile time — file changed since compilation?" p rows))
            sprintf "        size_t %s_col = 0, %s_pos = 0;" v v
            "        while (true) {"
            sprintf "            size_t %s_comma = %s_line.find(',', %s_pos);" v v v
            sprintf "            size_t %s_len = (%s_comma == std::string::npos ? %s_line.size() : %s_comma) - %s_pos;" v v v v v
            sprintf "            std::string %s_cell = %s_line.substr(%s_pos, %s_len);" v v v v
            sprintf "            if (%s_col >= %d) %s" v cols
                (csvExit v (sprintf "row at line \" << %s_lineno << \" of '%s' has more than %d cells" v p cols))
            sprintf "            const char* %s_cs = %s_cell.c_str(); char* %s_end = nullptr;" v v v ]
        @ convert
        @ [ sprintf "            if (%s_end == %s_cs || *%s_end != '\\0') %s" v v v
                (csvExit v (sprintf "non-numeric cell '\" << %s_cell << \"' in '%s' at line \" << %s_lineno << \"" v p v))
            sprintf "            %s_flat[%s_row * %d + %s_col] = (%s)%s_val;" v v cols v elemCpp v
            sprintf "            %s_col++;" v
            sprintf "            if (%s_comma == std::string::npos) break;" v
            sprintf "            %s_pos = %s_comma + 1;" v v
            "        }"
            sprintf "        if (%s_col != %d) %s" v cols
                (csvExit v (sprintf "row at line \" << %s_lineno << \" of '%s' has \" << %s_col << \" cells where %d were baked at compile time" v p v cols))
            sprintf "        %s_row++;" v
            "    }"
            sprintf "    if (%s_row != %d) %s" v rows
                (csvExit v (sprintf "'%s' has \" << %s_row << \" data rows where %d were baked at compile time — file changed since compilation?" p v rows))
            "}" ]

    /// Dense reader: parse-and-fill into `<v>_flat`, then the standard
    /// materialization (extents, allocate<>, flat->nested copy, release) —
    /// the same closing form as CppZarr.genReadVar.
    let genReadVar (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        if varName <> DataVarName then
            failwithf "CSV codegen: variable '%s' not found in '%s' (the only variable is '%s')" varName path DataVarName
        let f = loadMeta path
        let headered = match f.Shape with CsvTable _ -> true | CsvMatrix _ -> false
        let rows = int64 (match f.Shape with CsvTable (_, r) -> r | CsvMatrix (r, _) -> r)
        let cols = int64 (colCount f)
        let v = cppVarName
        let elemCpp = elemCppOf arrType.ElemType
        let isInt = (f.Elem = ETInt64)
        let assemble = genParseFill path v elemCpp isInt headered rows cols
        let materialize =
            [ sprintf "size_t %s_extent_0 = %d;" v rows
              sprintf "size_t %s_extent_1 = %d;" v cols
              sprintf "size_t %s_extents[] = { %s_extent_0, %s_extent_1 };" v v v
              sprintf "Array<%s, 2> %s = { allocate<typename promote<%s, 2>::type, nullptr>(%s_extents), %s_extents };" elemCpp v elemCpp v v
              sprintf "for (size_t %s_i0 = 0; %s_i0 < %s_extent_0; %s_i0++) {" v v v v
              sprintf "    for (size_t %s_i1 = 0; %s_i1 < %s_extent_1; %s_i1++) {" v v v v
              sprintf "        %s[%s_i0][%s_i1] = %s_flat[%s_i0 * %s_extent_1 + %s_i1];" v v v v v v v
              "    }"
              "}"
              sprintf "delete[] %s_flat;" v ]
        assemble @ materialize

    /// Dense writer: `<v>_flat` (populated by the codegen write intercept)
    /// streamed out as comma rows. Rank-1 writes one value per line (a
    /// column; re-loads as R x 1). Floats print at max_digits10 (17) with a
    /// FORCED decimal point so a whole-valued float column re-loads as
    /// Float64, not Int64; "nan"/"inf" renderings are left untouched (the
    /// reader accepts them).
    let genWriteVar (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (_dimNames: string list) : string list =
        let v = cppVarName
        let p = cppPath path
        arrType.IndexTypes |> List.iter (fun ix ->
            if ix.Symmetry <> SymNone || ix.Rank <> 1 then
                failwithf "CSV write of '%s': packed/compound index groups are not supported — densify first" varName)
        let litExtent (e: IRExpr) =
            match e with
            | IRLit (IRLitInt n) -> n
            | _ -> failwithf "CSV write of '%s' requires literal extents" varName
        let extents = arrType.IndexTypes |> List.map (fun ix -> litExtent ix.Extent)
        let (rows, cols) =
            match extents with
            | [r] -> (r, 1L)
            | [r; c] -> (r, c)
            | _ -> failwithf "CSV write of '%s': rank %d is not supported (rank 1 or 2 only)" varName extents.Length
        let elemCpp = elemCppOf arrType.ElemType
        let isInt = (elemCpp = "long long" || elemCpp = "int")
        let cellExpr =
            if isInt then
                [ sprintf "        if (%s_c) %s_out << ',';" v v
                  sprintf "        %s_out << %s_flat[%s_r * %d + %s_c];" v v v cols v ]
            else
                [ sprintf "        std::ostringstream %s_os;" v
                  sprintf "        %s_os << std::setprecision(17) << %s_flat[%s_r * %d + %s_c];" v v v cols v
                  sprintf "        std::string %s_s = %s_os.str();" v v
                  sprintf "        if (%s_s.find('.') == std::string::npos && %s_s.find('e') == std::string::npos && %s_s.find('n') == std::string::npos && %s_s.find('i') == std::string::npos) %s_s += \".0\";" v v v v v
                  sprintf "        if (%s_c) %s_out << ',';" v v
                  sprintf "        %s_out << %s_s;" v v ]
        [ sprintf "// Write %s to CSV %s (%d x %d, no header)" varName p rows cols
          "{"
          sprintf "    std::ofstream %s_out(\"%s\", std::ios::trunc);" v p
          sprintf "    if (!%s_out) %s" v (csvExit v (sprintf "cannot open '%s' for writing" p))
          sprintf "    for (size_t %s_r = 0; %s_r < %d; %s_r++) {" v v rows v
          sprintf "      for (size_t %s_c = 0; %s_c < %d; %s_c++) {" v v cols v ]
        @ cellExpr
        @ [ "      }"
            sprintf "      %s_out << '\\n';" v
            "    }"
            sprintf "    if (!%s_out.good()) %s" v (csvExit v (sprintf "write failed for '%s'" p))
            "}" ]

    /// Required C++ includes for CSV I/O (std only — no link flags).
    let genIncludes () : string list =
        [ "#include <fstream>"
          "#include <sstream>"
          "#include <string>"
          "#include <iostream>"
          "#include <iomanip>"
          "#include <cstdlib>" ]

// ============================================================================
// F#-side fixture writer (tests and programmatic file creation)
// ============================================================================

module CsvWrite =

    /// Exact-text control: caller supplies finished lines (no newlines
    /// inside); written LF-terminated.
    let writeRaw (path: string) (lines: string list) : unit =
        File.WriteAllText(path, (lines |> String.concat "\n") + "\n")

    /// Headered table from string cells (caller formats numbers).
    let writeTable (path: string) (header: string list) (rows: string list list) : unit =
        writeRaw path ((String.concat "," header) :: (rows |> List.map (String.concat ",")))

    /// Headerless float matrix; round-trip formatting ("R" gives shortest
    /// exact rendering) with a forced decimal point, mirroring the C++
    /// writer's dtype-stability rule.
    let writeMatrix (path: string) (data: float[][]) : unit =
        let cell (x: float) =
            let s = x.ToString("R", Globalization.CultureInfo.InvariantCulture)
            if s.Contains "." || s.Contains "e" || s.Contains "E" || s.Contains "N" || s.Contains "I" then s
            else s + ".0"
        writeRaw path (data |> Array.toList |> List.map (fun row -> row |> Array.map cell |> String.concat ","))

// ============================================================================
// Provider registration record
// ============================================================================

/// The csv ProviderSpec (surface module name: "csv"). Registered by
/// ProviderStatics.install ().
let spec : Blade.ProviderRegistry.ProviderSpec = {
    Name = "csv"
    LoadAsModule = loadAsModule
    ReadVarData = readVarData
    GenReadVar = CppCsv.genReadVar
    GenReadPacked = None       // packed groups: not representable in CSV
    GenReadCompoundVar = None  // load_compound: rejected loudly
    GenWriteVar = CppCsv.genWriteVar
    GenStreamOpen = None       // streaming: future arc (rejected loudly)
    GenStreamFiber = None
    Includes = CppCsv.genIncludes
    VarDimNames = fun _ _ -> None  // CSV carries no dimension names
    Fingerprint = fileFingerprint
    VersionStamp = fileVersionStamp
    LinkNeeds = "none (pure std C++17)"
}
