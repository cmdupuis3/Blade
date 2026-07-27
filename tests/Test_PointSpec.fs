// Pins for the point-group counting layer (MLPointSpec.fs) — stage 5b-0 of
// plan-transforms-as-types §3.6 / §7 stage 5. Everything here is integer:
// frozen table data, a closure enumeration, the FS indicators, and the
// e-weighted Hom formula. The exact-rational Hom-space Reynolds oracle that
// certifies the EMITTED BASIS against those counts is the separate "PG Oracle"
// block (tests/Test_PgOracle.fs), exactly as Perm Spec / Perm Oracle split.
//
// Five families of pin.
//
//  1. TABLE INTEGRITY. Fetching a group runs `certifyPointGroup`, so the smoke
//     test IS the assert battery: closure count vs declared order, closure
//     under multiplication, orthogonality of every element matrix, ν = 2 − e
//     per label, the J identities, and the ℝ-Burnside trap Σ dᵢ²/eᵢ = |G|.
//     This block additionally RE-DERIVES the FS indicators and the J identities
//     here, from the enumerated elements, so the numbers appear in the test
//     output rather than only inside a failwithf that never fires.
//
//  2. THE 9-vs-5 CONTRAST — the thesis of stage 5b as one assertion. Same spec
//     SHAPE ([trivial × 1, E × 2] → itself), same E dimension, same R₉₀
//     generator, and the answers differ because C4's E is of complex type
//     (e = 2) and D4's E is of real type (e = 1). The FS correction is the ONLY
//     input that differs between the two calls.
//
//  3. THE TWIN PIN (§3.6 "twin-not-reroute"). The generic e-weighted core,
//     instantiated at O(3) labels with EndDim ≡ 1, must agree with
//     MLSpec.homDim and with the MLSpec.homBlocks PAIR COUNT on a 15-spec
//     sweep (225 ordered pairs), and with MLSpec.totalDim on the specs. That is
//     what earns the abstraction WITHOUT rerouting O(3) through it: MLSpec is
//     byte-untouched, and this file is the only place the two modules meet.
//     MLPointSpec itself stays dependency-free — the `open Blade.ML` of MLSpec
//     happens HERE, in the test, and nowhere else.
//
//  4. THE FsQuat COUNT ARM. §3.6 reserves the VALUE: counting is uniform in e,
//     so a synthetic quaternionic label must weight a cell by 4 in the FS
//     formula, while every emission-adjacent path raises a loud internal error.
//     Both halves are pinned on a synthetic label that no registry ships.
//
//  5. THE SIZING SURFACE. pgTotalDim / pgHomBlocks structure, the
//     Σ_pairs mOut·mIn·e = pgHomDim identity, block starts, the zero case, and
//     the unknown-label failure.
module Blade.Tests.PointSpecReview

open Blade.Tests.TestHarness
open Blade.ML.PointSpec

module MS = Blade.ML.Spec

// ---------------------------------------------------------------------------
// The O(3) instantiation of the generic core, for the twin pin. The label type
// is (l, parity) — exactly MLSpec's aggregation key — and EndDim is constantly
// 1 because every O(3) irrep is of real type. That constant IS the reason
// MLSpec.homDim needs no correction factor.
// ---------------------------------------------------------------------------

let private o3Algebra : BlockAlgebra<int * int> =
    { Dim = fun (l, _) -> 2 * l + 1
      EndDim = fun _ -> 1 }

let private o3Spec (s: MS.Spec) : ((int * int) * int) list =
    s |> List.map (fun e -> ((e.L, e.Parity), e.Mult))

let private mk (l: int) (p: int) (m: int) : MS.SpecEntry = { L = l; Parity = p; Mult = m }

/// The 15-spec sweep: the sh_spec family, plain multiplicities, DUPLICATE
/// entries (the aggregation case MLSpec.aggregateByIrrep exists for), the two
/// parities at one l, the pseudoscalar, and the empty spec.
let private sweep : MS.Spec list =
    [ MS.shSpec 0
      MS.shSpec 1
      MS.shSpec 2
      MS.shSpec 3
      [ mk 0 0 2 ]
      [ mk 1 1 3 ]
      [ mk 0 0 1; mk 1 1 1 ]
      [ mk 1 1 2; mk 2 0 1 ]
      [ mk 0 0 1; mk 0 0 2 ]
      [ mk 2 0 1 ]
      [ mk 1 0 1 ]
      [ mk 0 0 1; mk 1 1 1; mk 2 0 1; mk 3 1 1 ]
      [ mk 1 1 1; mk 1 0 1 ]
      [ mk 0 1 1 ]
      [] ]

// ---------------------------------------------------------------------------
// A synthetic quaternionic label — NOT in any registry, and deliberately not a
// real group's table. It exists to exercise the two FsQuat arms: the counting
// one (uniform in e) and the emitting one (loud internal error).
// ---------------------------------------------------------------------------

let private quatLabel : PgIrrep =
    { Name = "Q"; DimR = 4; Fs = FsQuat; Gens = []; J = None }

let private quatAlgebra : BlockAlgebra<string> =
    { Dim = fun n -> if n = "Q" then 4 else 1
      EndDim = fun n -> if n = "Q" then endDim FsQuat else 1 }

let private raises (f: unit -> unit) : bool * string =
    try
        f ()
        (false, "no exception")
    with ex -> (true, ex.Message)

let private showSpec (s: PgSpec) : string =
    if List.isEmpty s then "[]"
    else s |> List.map (fun (n, m) -> sprintf "%s x %d" n m) |> String.concat ", "

let runPointSpecTests () : BlockResult =
    printHeader "PG Spec (point-group labels, the FS-weighted count)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail

    // =======================================================================
    // 1. TABLE INTEGRITY — the fetch itself is the assert battery
    // =======================================================================
    let fetched = raises (fun () -> pointGroup "C4" |> ignore; pointGroup "D4" |> ignore)
    check "registry: fetching C4 and D4 runs certifyPointGroup without a single assert firing"
          (not (fst fetched))
          (if fst fetched then snd fetched else sprintf "registry = {%s}" (String.concat ", " pointGroupNames))

    let c4 = pointGroup "C4"
    let d4 = pointGroup "D4"
    let c4i = certifyPointGroup c4
    let d4i = certifyPointGroup d4

    check "C4: generator closure over the common word set has |G| = 4 elements"
          (c4i.Closure = 4 && c4.Order = 4)
          (sprintf "words: %s" (String.concat " " c4i.Words))
    check "D4: generator closure over the common word set has |G| = 8 elements"
          (d4i.Closure = 8 && d4.Order = 8)
          (sprintf "words: %s" (String.concat " " d4i.Words))

    // The FS indicators, printed rather than merely asserted. nu = 2 - e:
    // 1 at real type, 0 at complex type (see MLPointSpec.fsIndicator for why
    // the quaternionic row would read -2 and not the textbook -1).
    let showNu (xs: (string * int) list) =
        xs |> List.map (fun (n, v) -> sprintf "%s=%d" n v) |> String.concat " "
    check "C4 FS indicators sum_g chi(g^2)/|G|: A=1 B=1 E=0 — E is of COMPLEX type, e = 2"
          (c4i.FsIndicators = [ ("A", 1); ("B", 1); ("E", 0) ]
           && endDim (pgIrrep c4 "E").Fs = 2)
          (showNu c4i.FsIndicators)
    check "D4 FS indicators sum_g chi(g^2)/|G|: A1=A2=B1=B2=E=1 — E is of REAL type, e = 1"
          (d4i.FsIndicators = [ ("A1", 1); ("A2", 1); ("B1", 1); ("B2", 1); ("E", 1) ]
           && endDim (pgIrrep d4 "E").Fs = 1)
          (showNu d4i.FsIndicators)
    // Re-derived here from the enumerated elements, so the indicator is a
    // computation this block performed and not only one it trusted.
    let c4els = groupElements c4
    let d4els = groupElements d4
    let nuAgain =
        (c4.Irreps |> List.mapi (fun i ir -> (ir.Name, fsIndicator c4 c4els i))) = c4i.FsIndicators
        && (d4.Irreps |> List.mapi (fun i ir -> (ir.Name, fsIndicator d4 d4els i))) = d4i.FsIndicators
    check "FS indicators re-derived from groupElements agree with the certificate" nuAgain ""

    // The J identities, spelled out at the one label that has one.
    let jE = (pgIrrep c4 "E").J
    let jOk =
        match jE with
        | Some j ->
            matEq (matMul j j) (matNeg (matId 2))
            && ((pgIrrep c4 "E").Gens |> List.forall (fun g -> matEq (matMul j g) (matMul g j)))
        | None -> false
    check "C4::E carries a baked J with J^2 = -Id and J*r = r*J (J = R90, the generator itself)"
          (jOk && c4i.JLabels = [ "E" ])
          (sprintf "J = %A" (match jE with Some j -> j | None -> [||]))
    // ABSENCE IS DATA, not a proof obligation: this pins that D4's E declares
    // no J (and that its real type is what makes that consistent). Nothing here
    // asserts that no valid J could exist — that content is carried positively
    // by nu(D4::E) = 1.
    check "D4::E declares NO J, and its real type is what makes that consistent (absence asserted as DATA)"
          ((pgIrrep d4 "E").J = None && d4i.JLabels = [] && (pgIrrep d4 "E").Fs = FsReal) ""

    check "R-Burnside trap: sum_i d_i^2/e_i = |G| — C4: 1+1+4/2 = 4, D4: 1+1+1+1+4 = 8"
          (c4i.Burnside = 4 && d4i.Burnside = 8 && burnsideSum c4 = c4.Order && burnsideSum d4 = d4.Order)
          (sprintf "C4 %d = %d, D4 %d = %d" c4i.Burnside c4.Order d4i.Burnside d4.Order)
    check "endDim: R -> 1, C -> 2, H -> 4"
          (endDim FsReal = 1 && endDim FsComplex = 2 && endDim FsQuat = 4) ""

    // The E blocks of the two groups share their rotation generator EXACTLY —
    // which is what makes the contrast below a pure FS effect rather than a
    // difference of tables.
    check "C4::E and D4::E share the same R90 generator; D4::E adds the mirror diag(1,-1)"
          (matEq (List.head (pgIrrep c4 "E").Gens) (List.head (pgIrrep d4 "E").Gens)
           && List.length (pgIrrep d4 "E").Gens = 2
           && matEq (List.item 1 (pgIrrep d4 "E").Gens) [| [| 1; 0 |]; [| 0; -1 |] |]) ""

    // =======================================================================
    // 2. THE 9-vs-5 CONTRAST PIN — THE THESIS OF STAGE 5b
    //
    // One spec SHAPE, [trivial x 1, E x 2] -> itself, evaluated against the two
    // groups. Both E labels are 2-dimensional, both are generated by the same
    // R90 matrix, both specs carry the same multiplicities, and both trivial
    // labels are 1-dimensional. The ONLY input that differs is e(E): 2 at C4
    // (complex type) and 1 at D4 (real type). So
    //
    //     C4:  1*1*1 + 2*2*2 = 1 + 8 = 9
    //     D4:  1*1*1 + 2*2*1 = 1 + 4 = 5
    //
    // and the difference IS the Frobenius-Schur correction, with nothing else
    // varying that could account for it. A count of 5 at C4 would be the naive
    // Sum m_i*n_i formula that O(3) let us get away with for the whole of
    // stages 1-4; negative control (ii) of the PG Oracle block asserts that
    // naive formula is visibly wrong here.
    // =======================================================================
    let c4Spec : PgSpec = [ ("A", 1); ("E", 2) ]
    let d4Spec : PgSpec = [ ("A1", 1); ("E", 2) ]
    let c4Hom = pgHomDim c4 c4Spec c4Spec
    let d4Hom = pgHomDim d4 d4Spec d4Spec
    check "THE CONTRAST: [trivial x1, E x2] -> itself is 9 at C4 and 5 at D4 — the FS correction is the only difference"
          (c4Hom = 9 && d4Hom = 5)
          (sprintf "C4 %s -> %d (1 + 2*2*2), D4 %s -> %d (1 + 2*2*1)"
               (showSpec c4Spec) c4Hom (showSpec d4Spec) d4Hom)
    check "the contrast is NOT a dimension effect: both modules are 5-dimensional over R"
          (pgTotalDim c4 c4Spec = 5 && pgTotalDim d4 d4Spec = 5
           && (pgIrrep c4 "E").DimR = (pgIrrep d4 "E").DimR)
          (sprintf "dim_R V = %d at both" (pgTotalDim c4 c4Spec))
    // And the same shape at the E label alone, where the ratio is starkest.
    check "[E x1] -> [E x1] is 2 at C4 and 1 at D4 — End_G(E) is C there and R here"
          (pgHomDim c4 [ ("E", 1) ] [ ("E", 1) ] = 2 && pgHomDim d4 [ ("E", 1) ] [ ("E", 1) ] = 1) ""

    // =======================================================================
    // 3. THE TWIN PIN — generic core @ O(3) = MLSpec, on a 15-spec sweep
    // =======================================================================
    let mutable homMismatch : string list = []
    let mutable blockMismatch : string list = []
    let mutable pairs = 0
    for si in sweep do
        for so in sweep do
            let gd = genericHomDim o3Algebra (o3Spec si) (o3Spec so)
            let md = MS.homDim si so
            if gd <> md then
                homMismatch <- sprintf "%A -> %A: generic %d vs MLSpec %d" si so gd md :: homMismatch
            let gb = genericHomBlocks o3Algebra (o3Spec si) (o3Spec so) |> List.length
            let mb = MS.homBlocks si so |> List.length
            if gb <> mb then
                blockMismatch <- sprintf "%A -> %A: generic %d pairs vs MLSpec %d" si so gb mb :: blockMismatch
            pairs <- pairs + 1
    check (sprintf "TWIN: genericHomDim @ O(3) (EndDim = 1) = MLSpec.homDim on all %d ordered spec pairs" pairs)
          (List.isEmpty homMismatch)
          (if List.isEmpty homMismatch then
             sprintf "%d specs, %d pairs, zero divergence — MLSpec byte-untouched" (List.length sweep) pairs
           else sprintf "%d mismatches, e.g. %s" homMismatch.Length (List.head homMismatch))
    check (sprintf "TWIN: genericHomBlocks @ O(3) pair count = MLSpec.homBlocks pair count on all %d pairs" pairs)
          (List.isEmpty blockMismatch)
          (if List.isEmpty blockMismatch then "zero divergence"
           else sprintf "%d mismatches, e.g. %s" blockMismatch.Length (List.head blockMismatch))
    let totalOk = sweep |> List.forall (fun s -> genericTotalDim o3Algebra (o3Spec s) = MS.totalDim s)
    check "TWIN: genericTotalDim @ O(3) = MLSpec.totalDim on the 15 sweep specs" totalOk ""
    // The identity that ties the two together at e = 1 AND at e = 2: summing
    // mOut*mIn*e over the emitted pairs reproduces the FS formula.
    let pairSumOk =
        sweep |> List.forall (fun si ->
            sweep |> List.forall (fun so ->
                let s = genericHomBlocks o3Algebra (o3Spec si) (o3Spec so)
                        |> List.sumBy (fun (_, _, (k, mOut), (_, mIn)) -> mOut * mIn * o3Algebra.EndDim k)
                s = MS.homDim si so))
    check "TWIN: sum over homBlocks pairs of mOut*mIn*e = homDim, all 225 pairs" pairSumOk ""

    // =======================================================================
    // 4. THE FsQuat COUNT ARM — the reserved value
    // =======================================================================
    let quatCount = genericHomDim quatAlgebra [ ("Q", 2) ] [ ("Q", 3) ]
    check "FsQuat COUNTS uniformly: a synthetic H-type label weights a cell by e = 4 (2*3*4 = 24)"
          (quatCount = 24 && endDim quatLabel.Fs = 4)
          (sprintf "genericHomDim [Q x2] -> [Q x3] = %d" quatCount)
    let (quatRaised, quatMsg) = raises (fun () -> endBasis quatLabel |> ignore)
    check "FsQuat REFUSES to emit: endBasis on an H-type label is a loud internal error"
          (quatRaised && quatMsg.Contains "RESERVED")
          quatMsg
    // The two shipped arms of endBasis, for contrast: length = e by construction.
    check "endBasis length = e: [Id] at D4::E (real), [Id, J] at C4::E (complex)"
          (List.length (endBasis (pgIrrep d4 "E")) = 1
           && List.length (endBasis (pgIrrep c4 "E")) = 2
           && matEq (List.item 1 (endBasis (pgIrrep c4 "E"))) (match (pgIrrep c4 "E").J with Some j -> j | None -> [||])) ""

    // =======================================================================
    // 5. THE SIZING SURFACE
    // =======================================================================
    check "pgTotalDim: C4 [A x2, B x1, E x3] = 2 + 1 + 6 = 9"
          (pgTotalDim c4 [ ("A", 2); ("B", 1); ("E", 3) ] = 9) ""
    check "pgBlockStarts: offsets scan the spec, last = pgTotalDim"
          (pgBlockStarts c4 [ ("A", 2); ("B", 1); ("E", 3) ] = [ 0; 2; 3; 9 ]) ""
    // homBlocks is ALL matching pairs, output-major, duplicates included — the
    // MLSpec.homBlocks contract, verbatim.
    let dupSpec : PgSpec = [ ("E", 1); ("A", 1); ("E", 2) ]
    let dupBlocks = pgHomBlocks c4 dupSpec dupSpec
    check "pgHomBlocks: all matching (in, out) block pairs, output-major, duplicate labels reachable"
          (List.length dupBlocks = 5
           && dupBlocks |> List.map (fun (bi, bo, _, _) -> (bi, bo))
              = [ (0, 0); (2, 0); (1, 1); (0, 2); (2, 2) ])
          (sprintf "%d pairs over spec [%s]" (List.length dupBlocks) (showSpec dupSpec))
    let pairSum =
        dupBlocks |> List.sumBy (fun (_, _, (label, mOut), (_, mIn)) ->
            mOut * mIn * endDim (pgIrrep c4 label).Fs)
    check "sum over pgHomBlocks pairs of mOut*mIn*e = pgHomDim (the emission/count bridge)"
          (pairSum = pgHomDim c4 dupSpec dupSpec)
          (sprintf "%d = %d" pairSum (pgHomDim c4 dupSpec dupSpec))
    // Duplicate entries AGGREGATE in the count (MLSpec.aggregateByIrrep's rule).
    check "duplicate spec entries aggregate: [E x1, E x2] counts as [E x3]"
          (pgHomDim c4 [ ("E", 1); ("E", 2) ] [ ("E", 3) ] = pgHomDim c4 [ ("E", 3) ] [ ("E", 3) ]
           && pgHomDim c4 [ ("E", 3) ] [ ("E", 3) ] = 18) ""
    check "THE ZERO CASE: C4 [B x1] -> [A x1] is 0 — distinct labels never connect"
          (pgHomDim c4 [ ("B", 1) ] [ ("A", 1) ] = 0
           && pgHomBlocks c4 [ ("B", 1) ] [ ("A", 1) ] = []) ""
    check "an output label absent from the input is simply absent from homBlocks (no failure)"
          (pgHomDim d4 [ ("E", 2) ] [ ("B1", 1); ("E", 1) ] = 2
           && List.length (pgHomBlocks d4 [ ("E", 2) ] [ ("B1", 1); ("E", 1) ]) = 1) ""
    check "the empty spec is legal and totals 0"
          (pgTotalDim c4 [] = 0 && pgHomDim c4 [] c4Spec = 0 && pgHomDim c4 c4Spec [] = 0) ""

    // Unknown labels and unknown groups fail loudly HERE (the source-level
    // decoder diagnostic is 5b-i's and does not route through this layer).
    let (unkLabel, unkLabelMsg) = raises (fun () -> pgHomDim c4 [ ("A1", 1) ] [ ("A", 1) ] |> ignore)
    check "unknown label is a loud failure naming the group's label set"
          (unkLabel && unkLabelMsg.Contains "A1" && unkLabelMsg.Contains "A, B, E")
          unkLabelMsg
    let (unkGroup, unkGroupMsg) = raises (fun () -> pointGroup "C3v" |> ignore)
    check "unknown group is a loud failure naming the registry"
          (unkGroup && unkGroupMsg.Contains "C4" && unkGroupMsg.Contains "D4")
          unkGroupMsg
    let (negMult, _) = raises (fun () -> pgTotalDim c4 [ ("A", -1) ] |> ignore)
    check "negative multiplicity is refused" negMult ""

    printFooter "PG Spec" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "PG Spec"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
