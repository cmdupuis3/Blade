// Structured C++ emission layer (audit §2.1) — THIN, deliberately not a
// C++ AST. Typed builders for the recurring emission shapes: named fields
// make argument transposition a compile error instead of a
// compiles-clean-but-wrong bug (the sprintf scatter loop this replaces
// threaded the same name through TWENTY positional %s slots).
//
// Policy (audit §2.1): any emission with more than two interpolated slots
// should go through a builder here. The pre-existing sprintf sites are
// being migrated shape-by-shape — recurring shapes first (loop headers,
// allocation, scatter); one-off low-slot sprintfs may stay put.
module Blade.EmitCpp

// ----------------------------------------------------------------------------
// Loop headers
// ----------------------------------------------------------------------------

/// `for (size_t VAR = 0; VAR < BOUND; VAR++) {` — the canonical counting
/// loop. VAR is stated once; the old sprintf form needed it three times.
let forLoop (ind: string) (var: string) (bound: string) : string =
    sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind var var bound var

/// `for (int64_t VAR = START; VAR < BOUND; VAR++) {` — the for-in loop.
/// int64_t, not size_t (unlike forLoop's internal counters): VAR is the
/// user's Int64 for-in variable, and an unsigned binding wraps negative
/// intermediates in body arithmetic (e.g. 0.5 * (k - 1) at k=0).
let forLoopFrom (ind: string) (var: string) (start: string) (bound: string) : string =
    sprintf "%sfor (int64_t %s = %s; %s < %s; %s++) {" ind var start var bound var

// ----------------------------------------------------------------------------
// Array allocation
// ----------------------------------------------------------------------------

/// `Array<ELEM, RANK> NAME = { allocate<typename promote<ELEM, RANK>::type,
///  SYMM>(EXTENTS), EXTENTS };` — the wrapper-allocation declaration.
/// `Strict = Some arg` selects allocate_strict with its extra template arg.
/// Elem and Rank each appear twice in the output, Extents twice — stating
/// them once here is the point.
type ArrayAlloc = {
    Ind: string            // leading indentation ("" when the caller indents)
    Elem: string           // C++ element type
    Rank: int
    Name: string           // C++ binding name
    Symm: string           // symmetry template arg ("nullptr" or a symm array)
    Strict: string option  // allocate_strict's strict-iteration template arg
    Extents: string        // extents array expression
}

let arrayAlloc (a: ArrayAlloc) : string =
    match a.Strict with
    | Some strictArg ->
        sprintf "%sArray<%s, %d> %s = { allocate_strict<typename promote<%s, %d>::type, %s, %s>(%s), %s };"
            a.Ind a.Elem a.Rank a.Name a.Elem a.Rank a.Symm strictArg a.Extents a.Extents
    | None ->
        sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s>(%s), %s };"
            a.Ind a.Elem a.Rank a.Name a.Elem a.Rank a.Symm a.Extents a.Extents

// ----------------------------------------------------------------------------
// Compact-pool scatter
// ----------------------------------------------------------------------------

/// The compound() prefix-popcount scatter: copy the mask-present leading
/// cells (each dragging Trail trailing elements) from the dense pool into
/// the compact pool, row-major. All working variables derive from Name.
/// The sprintf this replaces had 21 positional slots, 20 of them Name.
type CompactScatter = {
    Ind: string
    Name: string     // compact binding name; prefixes every local (_r/_c/_t/...)
    IdxName: string  // compound_index_t instance (supplies the mask vector)
}

let compactScatter (s: CompactScatter) : string =
    let n = s.Name
    // Assembled from small pieces rather than one 21-slot format string —
    // miscounting THAT argument list is precisely the bug class this layer
    // exists to kill.
    s.Ind
    + sprintf "{ size_t %s_r = 0; " n
    + sprintf "for (size_t %s_c = 0; %s_c < %s_grid; %s_c++) " n n s.IdxName n
    + sprintf "if (%s_maskvec[%s_c]) { " s.IdxName n
    + sprintf "for (size_t %s_t = 0; %s_t < %s_trail; %s_t++) " n n n n
    + sprintf "%s_compact[%s_r * %s_trail + %s_t] = %s_densepool[%s_c * %s_trail + %s_t]; " n n n n n n n n
    + sprintf "%s_r++; } }" n
