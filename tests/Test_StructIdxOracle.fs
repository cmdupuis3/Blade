// THIRD-ROUTE ORACLE for the constrained-record counting layer
// (src/StructIdxSpec.fs, stage C1 of the retired constrained-index-types plan).
//
// The 5a-i discipline, applied: this file shares NO CODE with StructIdxSpec.
// It calls exactly one function from it — `enumerateBox` — and everything it
// compares that call against is derived here, by a different algorithm, from
// data written here. No helper, no arithmetic and no ordering convention is
// imported. An enumerator and its test that share a shift, a comparison or a
// loop bound do not verify each other; they agree with each other.
//
// WHY A THIRD ROUTE AT ALL. StructIdxSpec already carries an INTERNAL
// certificate on every call: it counts the solution set twice, by flat
// box-filtering and by arrow-style heads-filtering, and asserts set, order and
// cardinality agreement between them. Those two routes are genuinely different
// algorithms, so the internal certificate is real. But both live in one
// module, were written together, and share the module's own notion of what a
// box IS — the shift from coordinates to values, the field ordering, the
// ascending convention. A bug in that shared notion is invisible to both. This
// file is the outside view: a plain recursive per-field extension over VALUES,
// which never forms a coordinate at all and so cannot inherit a shift error.
//
// WHAT IS COMPARED, and why each is separate:
//   * ORDER — list equality against the spec's entries. Order is the property
//     that catches offset and nesting bugs, because a wrong shift or a wrong
//     outermost field permutes entries while preserving the set.
//   * SET — set equality, compared separately so an order failure and a
//     membership failure are DISTINGUISHABLE in the output. If both fail the
//     enumeration is wrong; if only order fails the convention is wrong.
//   * CARD — recomputed here by counting, and asserted against both the
//     spec's reported Card and the spec's own entry count. This is what stops
//     a future closed-form cardinality optimization from silently disagreeing
//     with the list it claims to describe.
//   * HAND TABLES — for the anchor family the expected entries are written
//     out literally, in lex order, computed by a human and not by either
//     enumerator. Two agreeing programs can still both be wrong; a hand table
//     is the only check with an independent source.
//
// THE ANCHOR is the plan's CGm112: m1, m2, m_out each over {-1, 0, 1},
// constrained by m1 + m2 == m_out. Box 27, card 7, the 2/3/2 split by output
// value. The negative-lo boxes are load-bearing rather than decorative — a
// box whose low end is negative is precisely where a coordinate/value
// confusion produces wrong numbers instead of merely differently-ordered ones,
// and the asymmetric family below (lo = -2) is chosen so that no symmetry can
// mask a sign error.
module Blade.Tests.StructIdxOracle

open Blade.Tests.TestHarness
open Blade.StructIdxSpec

// ---------------------------------------------------------------------------
// Route 3: recursive per-field extension over VALUES.
//
// A field is (name, lo, hi) with BOTH ENDS INCLUSIVE. The recursion extends a
// prefix by every legal value of the next field, first field outermost, values
// ascending. There is no box volume, no linear index, no offset vector and no
// coordinate anywhere in this function — which is the entire point of it.
// ---------------------------------------------------------------------------
let rec private extendAll (fields: (string * int64 * int64) list) : int64 list list =
    match fields with
    | [] -> [ [] ]
    | (_, lo, hi) :: rest ->
        let tails = extendAll rest
        [ for v in lo .. hi do
            for t in tails do
                yield v :: t ]

/// Filter the full box by a predicate stated over NAME-KEYED cells, so the
/// predicate cannot depend on field order even accidentally.
let private oracleEntries
        (fields: (string * int64 * int64) list)
        (pred: (string * int64) list -> bool) : int64 list list =
    let names = fields |> List.map (fun (n, _, _) -> n)
    extendAll fields
    |> List.filter (fun row -> pred (List.zip names row))

/// Adapt a local field list to the spec's BoxField record. This is the ONLY
/// structural coupling to StructIdxSpec, and it is deliberately a dumb
/// transliteration with no arithmetic in it.
let private toBoxFields (fields: (string * int64 * int64) list) : BoxField list =
    fields |> List.map (fun (n, lo, hi) -> { Field = n; Lo = lo; Hi = hi })

let runStructIdxOracleTests () : BlockResult =
    printHeader "Struct Idx Oracle (third route)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    let show (rows: int64 list list) =
        rows
        |> List.map (fun r -> r |> List.map string |> String.concat ",")
        |> String.concat " | "

    /// Run both routes over one family and cross-check them four ways.
    /// `label` names the family in every result line so a failure says which.
    let compareRoutes label (fields: (string * int64 * int64) list)
                      (pred: (string * int64) list -> bool) =
        let mine = oracleEntries fields pred
        let theirs =
            enumerateBox label (toBoxFields fields)
                (fun cell -> Ok (pred cell))
        match theirs with
        | Error e ->
            check ($"{label}: StructIdxSpec.enumerateBox succeeds") false e
            None
        | Ok box ->
            check ($"{label}: enumerateBox succeeds") true
                  ($"card {box.Card}")
            // ORDER first: it is the strictly stronger claim, and when it
            // passes the set check below is guaranteed, so a lone SET failure
            // can only mean the two routes disagree on membership.
            check ($"{label}: entries agree in ORDER (list equality)")
                  (box.Entries = mine)
                  (if box.Entries = mine then $"{mine.Length} entries"
                   else $"spec [{(show box.Entries)}] vs oracle [{(show mine)}]")
            check ($"{label}: entries agree as a SET")
                  (Set.ofList box.Entries = Set.ofList mine)
                  ($"spec {box.Entries.Length}, oracle {mine.Length}")
            // Card is asserted against BOTH the oracle's count and the spec's
            // own list, so a closed-form card that drifts from the entries it
            // describes fails here rather than silently downstream.
            check ($"{label}: Card equals the oracle's count")
                  (box.Card = mine.Length)
                  ($"spec Card {box.Card}, oracle count {mine.Length}")
            check ($"{label}: Card equals the spec's OWN entry count")
                  (box.Card = box.Entries.Length)
                  ($"Card {box.Card}, |Entries| {box.Entries.Length}")
            Some (box, mine)

    // -----------------------------------------------------------------------
    // 1. THE ANCHOR — CGm112. Hand table first, so the two enumerators are
    //    being checked against arithmetic neither of them performed.
    // -----------------------------------------------------------------------
    let m3 = [ ("m1", -1L, 1L); ("m2", -1L, 1L); ("m_out", -1L, 1L) ]
    let cgPred (cell: (string * int64) list) =
        let g k = cell |> List.find (fun (n, _) -> n = k) |> snd
        g "m1" + g "m2" = g "m_out"

    // Written out by hand in lex order (m1 outermost, ascending). The pairs
    // whose sum leaves [-1, 1] — (-1,-1) and (1,1) — are the two of nine that
    // do not appear.
    let cgHand : int64 list list =
        [ [ -1L;  0L; -1L ]
          [ -1L;  1L;  0L ]
          [  0L; -1L; -1L ]
          [  0L;  0L;  0L ]
          [  0L;  1L;  1L ]
          [  1L; -1L;  0L ]
          [  1L;  0L;  1L ] ]

    match compareRoutes "CGm112" m3 cgPred with
    | None -> ()
    | Some (box, mine) ->
        check "CGm112: card is 7 (the plan's anchor value)" (box.Card = 7) (string box.Card)
        check "CGm112: spec entries equal the HAND table, in order"
              (box.Entries = cgHand) (show box.Entries)
        check "CGm112: oracle entries equal the HAND table, in order"
              (mine = cgHand) (show mine)
        // The 2/3/2 split by output value — the structure behind the 7, which
        // a card alone cannot confirm.
        let bySum v = box.Entries |> List.filter (fun r -> List.item 2 r = v) |> List.length
        check "CGm112: the 2/3/2 split by m_out = -1, 0, +1"
              (bySum -1L = 2 && bySum 0L = 3 && bySum 1L = 2)
              ($"{(bySum -1L)}/{(bySum 0L)}/{(bySum 1L)}")
        // Every entry actually satisfies the constraint, and every entry lies
        // in the box. Cheap, but it is the check that catches an enumerator
        // emitting a correctly-SIZED wrong set.
        check "CGm112: every emitted entry satisfies the predicate"
              (box.Entries |> List.forall (fun r ->
                    cgPred (List.zip [ "m1"; "m2"; "m_out" ] r))) ""
        check "CGm112: every emitted entry lies inside the declared box"
              (box.Entries |> List.forall (fun r ->
                    List.forall2 (fun v (_, lo, hi) -> v >= lo && v <= hi) r m3)) ""
        // Strictly ascending: lex order with no duplicates, in one assertion.
        // NoDup is a named property of the Coq ck arrow this layer instantiates.
        check "CGm112: entries are STRICTLY lex-ascending (ordered and NoDup)"
              (box.Entries |> List.pairwise |> List.forall (fun (a, b) -> a < b))
              ""
        // Guard against a vacuous order check: if the comparison could not
        // tell a permutation from the original, the order assertions above
        // would be worthless. With 7 distinct entries a reversal must differ.
        check "CGm112: the ORDER comparison is non-vacuous (reversal differs)"
              (List.rev box.Entries <> box.Entries) ""

    // -----------------------------------------------------------------------
    // 2. THE 3/7/9 SWEEP. Widening the output range admits the ±lo slices; the
    //    DIFFERENCES are pinned as well as the counts, because a systematic
    //    offset error cancels out of a single card but not out of the slice
    //    structure.
    // -----------------------------------------------------------------------
    let sweep lo =
        let fields = [ ("m1", -1L, 1L); ("m2", -1L, 1L); ("m_out", -lo, lo) ]
        compareRoutes ($"sweep lo={lo}") fields cgPred
    let cards =
        [ 0L; 1L; 2L ] |> List.map (fun lo ->
            match sweep lo with Some (b, _) -> b.Card | None -> -1)
    check "sweep: the 3/7/9 cardinalities (lo = 0, 1, 2)"
          (cards = [ 3; 7; 9 ])
          (cards |> List.map string |> String.concat "/")
    check "sweep: saturation at lo = 2 equals the dense pair count (2l1+1)(2l2+1) = 9"
          (List.item 2 cards = 3 * 3) ""
    check "sweep: the difference structure — +4 for the s = +/-1 slices, +2 for s = +/-2"
          (cards = [ 3; 7; 9 ]
           && List.item 1 cards - List.item 0 cards = 4
           && List.item 2 cards - List.item 1 cards = 2) ""

    // -----------------------------------------------------------------------
    // 3. THE OFFSET PROBE — an ASYMMETRIC, wholly negative-leaning box. The
    //    anchor is symmetric about zero, so a sign error in a shift can cancel
    //    there; here m1 ranges over [-2, 0] and the output over [-3, 1], so no
    //    symmetry exists to hide behind and a coordinate/value confusion
    //    produces visibly wrong VALUES rather than a reordering.
    // -----------------------------------------------------------------------
    let asym = [ ("m1", -2L, 0L); ("m2", -1L, 1L); ("m_out", -3L, 1L) ]
    let asymHand : int64 list list =
        [ [ -2L; -1L; -3L ]
          [ -2L;  0L; -2L ]
          [ -2L;  1L; -1L ]
          [ -1L; -1L; -2L ]
          [ -1L;  0L; -1L ]
          [ -1L;  1L;  0L ]
          [  0L; -1L; -1L ]
          [  0L;  0L;  0L ]
          [  0L;  1L;  1L ] ]
    match compareRoutes "asymmetric negative box" asym cgPred with
    | None -> ()
    | Some (box, _) ->
        check "asymmetric: card 9 (every pair's sum is representable)" (box.Card = 9) (string box.Card)
        check "asymmetric: entries equal the HAND table, in order — the offset pin"
              (box.Entries = asymHand) (show box.Entries)
        check "asymmetric: the smallest entry is the box's low corner (-2, -1, -3)"
              (List.head box.Entries = [ -2L; -1L; -3L ]) (show [ List.head box.Entries ])

    // -----------------------------------------------------------------------
    // 4. UNCONSTRAINED — card must be the box VOLUME, and the entries the
    //    whole box in lex order. The closed-form pin the plan asks for, and
    //    the case that proves the filter is what removes cells elsewhere.
    // -----------------------------------------------------------------------
    let alwaysTrue (_: (string * int64) list) = true
    match compareRoutes "unconstrained 3x3x3" m3 alwaysTrue with
    | None -> ()
    | Some (box, _) ->
        check "unconstrained: card equals the box volume 3*3*3 = 27" (box.Card = 27) (string box.Card)
        check "unconstrained: first entry is the low corner, last is the high corner"
              (List.head box.Entries = [ -1L; -1L; -1L ]
               && List.last box.Entries = [ 1L; 1L; 1L ]) ""

    // -----------------------------------------------------------------------
    // 5. EMPTY — a derived-empty solution set is COUNTED, not refused. C1
    //    emits no warning for it (that is C2's); `enumerateBox` must return Ok
    //    with card 0. Schur-zero-style emptiness is legitimate mathematics.
    // -----------------------------------------------------------------------
    let never (_: (string * int64) list) = false
    match compareRoutes "empty solution set" m3 never with
    | None -> ()
    | Some (box, _) ->
        check "empty: card 0 and no entries, returned as Ok (C1-legal, not an error)"
              (box.Card = 0 && box.Entries.IsEmpty) (string box.Card)

    // -----------------------------------------------------------------------
    // 6. DEGENERATE SHAPES. Single field; a pinned field (lo = hi) which must
    //    contribute exactly one value rather than zero or two — the classic
    //    inclusive-bounds off-by-one; and a pinned field in the MIDDLE of a
    //    box, where a volume computed as (hi - lo) rather than (hi - lo + 1)
    //    would collapse the whole enumeration to nothing.
    // -----------------------------------------------------------------------
    match compareRoutes "single field [-1, 1]" [ ("m", -1L, 1L) ] alwaysTrue with
    | None -> ()
    | Some (box, _) ->
        check "single field: card 3 and entries [-1], [0], [1] in order"
              (box.Card = 3 && box.Entries = [ [ -1L ]; [ 0L ]; [ 1L ] ]) (show box.Entries)

    match compareRoutes "pinned field lo = hi" [ ("k", 4L, 4L) ] alwaysTrue with
    | None -> ()
    | Some (box, _) ->
        check "pinned field: an inclusive lo = hi contributes exactly ONE value"
              (box.Card = 1 && box.Entries = [ [ 4L ] ]) (show box.Entries)

    match compareRoutes "pinned middle field"
                        [ ("a", 0L, 1L); ("p", 7L, 7L); ("b", 0L, 1L) ] alwaysTrue with
    | None -> ()
    | Some (box, _) ->
        check "pinned middle field: card 2*1*2 = 4, the pin does not collapse the box"
              (box.Card = 4) (string box.Card)
        check "pinned middle field: the pinned coordinate is 7 in every entry"
              (box.Entries |> List.forall (fun r -> List.item 1 r = 7L)) (show box.Entries)

    // -----------------------------------------------------------------------
    // 7. PREDICATE FAILURE PROPAGATES. A conjunct that cannot be decided is
    //    not the same thing as a conjunct that is false: the first must fail
    //    the enumeration (naming the witness cell), the second must exclude
    //    the cell. Conflating them would silently shrink solution sets, which
    //    is the worst available failure mode for this layer — a wrong answer
    //    that looks like a right one.
    // -----------------------------------------------------------------------
    let boom (cell: (string * int64) list) =
        let g k = cell |> List.find (fun (n, _) -> n = k) |> snd
        if g "m1" = 0L && g "m2" = 0L then Error "conjunct did not fold"
        else Ok (g "m1" + g "m2" = g "m_out")
    let failed3 = enumerateBox "undecidable" (toBoxFields m3) boom
    check "an undecidable cell FAILS the enumeration (it is not treated as false)"
          (match failed3 with Error _ -> true | Ok _ -> false)
          (match failed3 with
           | Ok b -> $"returned Ok with card {b.Card}"
           | Error e -> e)

    printFooter "Struct Idx Oracle (third route)"
        [ $"{passed} passed"; $"{failed} failed" ]
    { Block = "Struct Idx Oracle"; Passed = passed; Failed = failed
      Skipped = 0; FailedNames = failedNames }
