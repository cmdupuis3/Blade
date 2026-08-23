// Structured C++ emission layer (audit section 2.1) -- THIN, deliberately not a
// C++ AST. Typed builders for the recurring emission shapes: named fields
// make argument transposition a compile error instead of a
// compiles-clean-but-wrong bug (the sprintf scatter loop this replaces
// threaded the same name through TWENTY positional %s slots).
//
// Policy (audit section 2.1): any emission with more than two interpolated slots
// should go through a builder here. The pre-existing sprintf sites are
// being migrated shape-by-shape -- recurring shapes first (loop headers,
// allocation, scatter); one-off low-slot sprintfs may stay put.
module Blade.EmitCpp

// ----------------------------------------------------------------------------
// Loop headers
// ----------------------------------------------------------------------------

/// `for (size_t VAR = 0; VAR < BOUND; VAR++) {` -- the canonical counting
/// loop. VAR is stated once rather than repeated at each use.
let forLoop (ind: string) (var: string) (bound: string) : string =
    $$"""{{ind}}for (size_t {{var}} = 0; {{var}} < {{bound}}; {{var}}++) {"""

/// `for (int64_t VAR = START; VAR < BOUND; VAR++) {` -- the for-in loop.
/// int64_t, not size_t (unlike forLoop's internal counters): VAR is the
/// user's Int64 for-in variable, and an unsigned binding wraps negative
/// intermediates in body arithmetic (e.g. 0.5 * (k - 1) at k=0).
let forLoopFrom (ind: string) (var: string) (start: string) (bound: string) : string =
    $$"""{{ind}}for (int64_t {{var}} = {{start}}; {{var}} < {{bound}}; {{var}}++) {"""

// ----------------------------------------------------------------------------
// Array allocation
// ----------------------------------------------------------------------------

/// `Array<ELEM, RANK> NAME = { allocate<typename promote<ELEM, RANK>::type,
///  SYMM>(EXTENTS), EXTENTS };` -- the wrapper-allocation declaration.
/// `Strict = Some arg` selects allocate_strict with its extra template arg.
/// Elem and Rank each appear twice in the output, Extents twice -- stating
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
        $"{a.Ind}Array<{a.Elem}, {a.Rank}> {a.Name} = {{ allocate<typename promote<{a.Elem}, {a.Rank}>::type, {a.Symm}>({a.Extents}), {a.Extents} }};"

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
    /// Is the trailing extent STATICALLY 1 (no trailing dims)? Then the copy
    /// is one element per grid cell and the BRANCHLESS form is emitted -- see
    /// the note below for why that is gated on this and not emitted always.
    ScalarTrail: bool
}

let compactScatter (s: CompactScatter) : string =
    let n = s.Name
    // Assembled from small pieces rather than one 21-slot format string --
    // miscounting THAT argument list is precisely the bug class this layer
    // exists to kill.
    if s.ScalarTrail then
        // BRANCHLESS COMPACTION. `mask()` produces a data-dependent predicate,
        // so the branchy form below mispredicts once per grid cell: measured at
        // 3.17 ns/cell against 0.72 branchless at 50% random selectivity, and
        // the excess closes exactly as 12-16 cycles per mispredict, the Zen 3
        // penalty (src/microkernels/stream_compaction.c). gcc does NOT
        // if-convert this loop -- verified in disassembly, byte-identical with
        // if-conversion disabled -- so the branch is really there.
        //
        // The trick: store UNCONDITIONALLY and advance the output cursor by the
        // predicate. An unselected cell writes to the slot the next selected
        // cell will overwrite, so the result is identical -- this is pure data
        // movement, no arithmetic, hence BITWISE exact and licence-free.
        //
        // GATED ON A SCALAR TRAIL, deliberately. With `trail` elements dragged
        // per cell the branchless form copies `grid * trail` instead of
        // `selected * trail`, which loses outright at low selectivity, and the
        // branch is amortised over the inner loop anyway. Only `trail == 1`,
        // known here at emission time, gets it.
        //
        // The caller MUST size the destination `cardinality + 1`: after the
        // last selected cell the cursor sits at `cardinality`, and a trailing
        // unselected cell writes there. That padding is an ABI requirement of
        // this form, not an implementation detail.
        s.Ind
        + $$"""{ size_t {{n}}_r = 0; """
        + $$"""for (size_t {{n}}_c = 0; {{n}}_c < {{s.IdxName}}_grid; {{n}}_c++) { """
        + $"{n}_compact[{n}_r] = {n}_densepool[{n}_c]; "
        + $$"""{{n}}_r += (size_t)(!!{{s.IdxName}}_maskvec[{{n}}_c]); } }"""
    else
        s.Ind
        + $$"""{ size_t {{n}}_r = 0; """
        + $"for (size_t {n}_c = 0; {n}_c < {s.IdxName}_grid; {n}_c++) "
        + $$"""if ({{s.IdxName}}_maskvec[{{n}}_c]) { """
        + $"for (size_t {n}_t = 0; {n}_t < {n}_trail; {n}_t++) "
        + $"{n}_compact[{n}_r * {n}_trail + {n}_t] = {n}_densepool[{n}_c * {n}_trail + {n}_t]; "
        + $$"""{{n}}_r++; } }"""
