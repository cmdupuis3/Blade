// Pins for the Sym^j(V_l) occurrence tables (SymPowerTables.fs) — stage 2b-i
// of the retired transforms-as-types plan §3.3b/§7 3b. The exact half re-verifies the
// integer/rational claims from OUTSIDE the builder (E·v = 0, occurrence
// counts vs the stage-2a weight-peel, the diagonal rational Gram); the float
// half pins global orthonormality under identity (3)'s /N_I weighting, the
// DERIVED realization phase rule (real iff j·l + L even; −i otherwise) with
// its gapped guard, and bit-pins small tables. The block also carries the
// §6.9(iii) extension: realCGDense completeness/exchange pins over the
// k ≤ 4 chain range (l1 ≤ 9, l2 ≤ 3, l3 ≤ 12).
//
// Stage 2b-ii appends the LABEL layer (MLSpec.polyLabels): the integer
// enumeration of the Sym^k basis and its counting theorems. Pinned here: the
// enumeration-order convention 2b-iii bakes, the shared occurrence order
// (MLSpec cannot call this module, so the two are pinned against each other),
// the §6.9(v) label ↔ stage-1 kept-cell alignment at k = 2 (count level), a
// 15-spec sweep against `sym_spec`, and the degenerate k ∈ {0, 1} cases.
module Blade.Tests.SymPowerTablesReview

open Blade.Tests.TestHarness
open Blade.ML.SymPowerTables

module MLS = Blade.ML.Spec
module WT = Blade.ML.WignerTables

let runSymPowerTablesTests () : BlockResult =
    printHeader "SymPower Tables (T_{j,l} label basis)"
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

    // ---- per-(j,l) exact + float pins, j ≤ 4, l ≤ 3 -----------------------
    for j in 1 .. 4 do
        for l in 0 .. 3 do
            let exact = symPowerExact j l
            let tbl = symPowerTable j l
            let nCells = tbl.Cells.Length

            // Integer E·v = 0, re-verified exactly (pre-float) through the
            // public applyE hook for every RREF row and every GS row.
            let allKilled =
                exact |> List.forall (fun ws ->
                    Seq.append ws.RrefRows ws.GsRows
                    |> Seq.forall (fun v -> applyE j l v |> Array.forall Rat.isZero))
            check (sprintf "T_{%d,%d}: E·v = 0 exact (all RREF+GS rows)" j l) allKilled ""

            // Occurrence counts per L = powerSpec [(l,p,1)] j multiplicities.
            let got = exact |> List.map (fun ws -> ws.L, ws.RrefRows.Length) |> Map.ofList
            let want =
                MLS.powerSpec MLS.PowSym [ ({ L = l; Parity = 0; Mult = 1 } : MLS.SpecEntry) ] j
                |> List.filter (fun e -> e.Mult > 0)
                |> List.map (fun e -> e.L, e.Mult)
                |> Map.ofList
            let countsStr =
                got |> Map.toList |> List.sortByDescending fst
                    |> List.map (fun (lL, m) -> sprintf "%d:%d" lL m) |> String.concat " "
            check (sprintf "T_{%d,%d}: occurrence counts = powerSpec" j l) (got = want)
                  (sprintf "L:mult = %s" countsStr)

            // Exact rational Gram within each multiplicity space: pairwise
            // gramDot of the GS rows is 0 off-diagonal and Norm2 on-diagonal
            // — Gram = I after the (positive) normalization, checked BEFORE
            // any float exists.
            let gramExactOk =
                exact |> List.forall (fun ws ->
                    let n = ws.GsRows.Length
                    seq { for r in 0 .. n - 1 do for s in 0 .. n - 1 -> (r, s) }
                    |> Seq.forall (fun (r, s) ->
                        let d = gramDot j l ws.GsRows.[r] ws.GsRows.[s]
                        if r = s then d = ws.Norm2.[r] && not (Rat.isZero d) else Rat.isZero d))
            check (sprintf "T_{%d,%d}: exact rational Gram = I per multiplicity space" j l) gramExactOk ""

            // Float Gram = I across ALL rows of the whole (j,l) family —
            // identity (3): Σ_I T[r,I]·T[r',I]/N_I = δ, including cross-L and
            // cross-copy pairs (global orthonormality of the emitted basis).
            let rows = [| for o in tbl.Occurrences do yield! o.Rows |]
            let mutable dev = 0.0
            for a in 0 .. rows.Length - 1 do
                for b in a .. rows.Length - 1 do
                    let mutable s = 0.0
                    for i in 0 .. nCells - 1 do
                        s <- s + rows.[a].[i] * rows.[b].[i] / tbl.CellMult.[i]
                    dev <- max dev (abs (s - (if a = b then 1.0 else 0.0)))
            check (sprintf "T_{%d,%d}: float Gram = I over all %d rows (1e-14)" j l rows.Length)
                  (dev < 1e-14) (sprintf "max dev %g" dev)

            // Phase rule per occurrence: the realized branch is the derived
            // one ((j·l+L) parity), it matches the empirical branch, and the
            // two branches are gapped — min residual < 1e-10·‖T‖, the other
            // branch > 0.1·‖T‖ with ‖T‖ = the complex block's max entry.
            let mutable worstRatio = 0.0
            let phaseOk =
                tbl.Occurrences |> List.forall (fun o ->
                    let big = max o.MaxRe o.MaxIm
                    let small = min o.MaxRe o.MaxIm
                    worstRatio <- max worstRatio (small / big)
                    o.Flipped = ((j * l + o.L) % 2 = 1)
                    && (o.MaxIm > o.MaxRe) = o.Flipped
                    && small < 1e-10 * big
                    && big > 0.1 * big && big > 0.05)
            check (sprintf "T_{%d,%d}: phase rule (j·l+L parity) + 5-order gap" j l) phaseOk
                  (sprintf "worst min/max residual ratio %g" worstRatio)

    // ---- j = 1 must be the identity table ---------------------------------
    let mutable idDev = 0.0
    for l in 0 .. 3 do
        let tbl = symPowerTable 1 l
        match tbl.Occurrences with
        | [ o ] when o.L = l ->
            for c in 0 .. 2 * l do
                for i in 0 .. 2 * l do
                    idDev <- max idDev (abs (o.Rows.[c].[i] - (if c = i then 1.0 else 0.0)))
        | _ -> idDev <- 1.0
    check "T_{1,l} = identity for l ≤ 3 (1e-14)" (idDev < 1e-14) (sprintf "max dev %g" idDev)

    // ---- T_{2,l}: even L only (Sym² carries no odd L) ----------------------
    let evenOnly =
        [ 0 .. 3 ] |> List.forall (fun l ->
            symPowerExact 2 l |> List.forall (fun ws -> ws.L % 2 = 0))
    check "T_{2,l}: occurrences are even-L only (l ≤ 3)" evenOnly ""

    // ---- the named first-new-case: V₃ ⊂ Sym³(V₂) is the −i branch ---------
    let s32 = symPowerTable 3 2
    let v3 = s32.Occurrences |> List.filter (fun o -> o.L = 3)
    check "V3 in Sym3(V2): exists, mult 1, realized from the imaginary branch (−i)"
          (v3.Length = 1 && v3.Head.Flipped && v3.Head.MaxIm > v3.Head.MaxRe)
          (sprintf "maxRe %g, maxIm %g" (v3 |> List.tryHead |> Option.map (fun o -> o.MaxRe) |> Option.defaultValue nan)
                                        (v3 |> List.tryHead |> Option.map (fun o -> o.MaxIm) |> Option.defaultValue nan))

    // ---- T_{3,1} L=1: the |v|²·v line --------------------------------------
    // Weight-1 kernel of Sym³(V_1) is 1-dimensional; in divided-power
    // coordinates (cells lex over i = m+1) the RREF vector is
    //   v = w₋₁·w₊₁² − w₀²·w₊₁   (pivot monomial (0,2,2) = w₋₁w₊₁²),
    // i.e. +1 at cell [0;2;2] and −1 at cell [1;1;2].
    let t31 = symPowerExact 3 1
    let l1ws = t31 |> List.filter (fun ws -> ws.L = 1)
    let t31Ok =
        match l1ws with
        | [ ws ] when ws.RrefRows.Length = 1 ->
            let cells = symCells 3 1 |> List.toArray
            let v = ws.RrefRows.[0]
            ws.Pivots.[0] = [0; 2; 2]
            && Array.forall2 (fun (cell: int list) (x: Rat) ->
                   if cell = [0; 2; 2] then x = Rat.one
                   elif cell = [1; 1; 2] then x = Rat.neg Rat.one
                   else Rat.isZero x) cells v
        | _ -> false
    check "T_{3,1} L=1: 1-dim kernel, pivot w-1*w+1^2, v = w-1*w+1^2 - w0^2*w+1" t31Ok ""

    // ---- bit-pins: exact float lists frozen from this construction ---------
    // A change here is a CONVENTION break (cell order, RREF labeling, GS
    // order, phase, normalization), not a tolerance issue.
    let bitRow (expected: float[]) (actual: float[]) =
        expected.Length = actual.Length && Array.forall2 (fun (a: float) b -> a = b) expected actual

    // T_{2,1}: rows in module order — occurrence L=2 rows c=0..4, then
    // L=0 — over cells [0;0] [0;1] [0;2] [1;1] [1;2] [2;2].
    let t21Expected : float[][] = [|
        [| 0.0; 0.0; 1.4142135623730947; 0.0; 0.0; 0.0 |]
        [| 0.0; 1.4142135623730949; 0.0; 0.0; 0.0; 0.0 |]
        [| -0.40824829046386302; 0.0; 0.0; 0.81649658092772637; 0.0; -0.40824829046386302 |]
        [| 0.0; 0.0; 0.0; 0.0; 1.4142135623730949; 0.0 |]
        [| -0.70710678118654735; 0.0; 0.0; 0.0; 0.0; 0.70710678118654735 |]
        [| -0.57735026918962573; 0.0; 0.0; -0.57735026918962595; 0.0; -0.57735026918962573 |]
    |]
    let t21Rows = [| for o in (symPowerTable 2 1).Occurrences do yield! o.Rows |]
    check "bit-pin: T_{2,1} complete (6 rows x 6 cells)"
          (t21Expected.Length = t21Rows.Length && Array.forall2 bitRow t21Expected t21Rows) ""

    // T_{3,1}: rows in module order — L=3 (7 rows) then L=1 (3 rows) — over
    // the 10 lex cells of Sym³ on 3 components.
    let t31Expected : float[][] = [|
        [| -0.49999999999999983; 0.0; 0.0; 0.0; 0.0; 1.4999999999999993; 0.0; 0.0; 0.0; 0.0 |]
        [| 0.0; 0.0; 0.0; 0.0; 2.4494897427831779; 0.0; 0.0; 0.0; 0.0; 0.0 |]
        [| -0.38729833462074148; 0.0; 0.0; 1.549193338482967; 0.0; -0.38729833462074148; 0.0; 0.0; 0.0; 0.0 |]
        [| 0.0; -0.94868329805051377; 0.0; 0.0; 0.0; 0.0; 0.63245553203367599; 0.0; -0.94868329805051377; 0.0 |]
        [| 0.0; 0.0; -0.38729833462074148; 0.0; 0.0; 0.0; 0.0; 1.549193338482967; 0.0; -0.38729833462074148 |]
        [| 0.0; -1.2247448713915887; 0.0; 0.0; 0.0; 0.0; 0.0; 0.0; 1.2247448713915887; 0.0 |]
        [| 0.0; 0.0; -1.4999999999999993; 0.0; 0.0; 0.0; 0.0; 0.0; 0.0; 0.49999999999999983 |]
        [| -0.77459666924148296; 0.0; 0.0; -0.7745966692414834; 0.0; -0.77459666924148296; 0.0; 0.0; 0.0; 0.0 |]
        [| 0.0; -0.77459666924148307; 0.0; 0.0; 0.0; 0.0; -0.7745966692414834; 0.0; -0.77459666924148307; 0.0 |]
        [| 0.0; 0.0; -0.77459666924148296; 0.0; 0.0; 0.0; 0.0; -0.7745966692414834; 0.0; -0.77459666924148296 |]
    |]
    let t31Rows = [| for o in (symPowerTable 3 1).Occurrences do yield! o.Rows |]
    check "bit-pin: T_{3,1} complete (10 rows x 10 cells)"
          (t31Expected.Length = t31Rows.Length && Array.forall2 bitRow t31Expected t31Rows) ""

    // One row of T_{4,2}: first occurrence (L=8, copy 0), row c=0, as its
    // nonzero (cell index, value) pairs over the 70 lex cells.
    let t42Expected : (int * float)[] = [|
        (4, -1.4142135623730945)
        (34, 1.4142135623730945)
    |]
    let t42Row = ((symPowerTable 4 2).Occurrences.Head).Rows.[0]
    let t42Nz = [| for i in 0 .. t42Row.Length - 1 do if t42Row.[i] <> 0.0 then yield (i, t42Row.[i]) |]
    check "bit-pin: T_{4,2} L=8 copy 0 row c=0 (nonzero cells)"
          (t42Expected.Length = t42Nz.Length
           && Array.forall2 (fun (i, v) (i', v') -> i = i' && (v: float) = v') t42Expected t42Nz) ""

    // ---- §6.9(iii): realCG completeness over the k ≤ 4 chain range ----------
    // The identity pinned: for fixed (l1, l2) the stacked real coupling
    // blocks over ALL valid l3 form a real ORTHOGONAL change of basis
    // V_{l1}⊗V_{l2} → ⊕_{l3} V_{l3} (complex CG unitarity conjugated by the
    // per-l uMatrix unitaries; the −i on odd triples is unit-modulus), so
    //   Σ_{l3=|l1−l2|}^{l1+l2} Σ_{c3} T^{l3}[c1,c2,c3]·T^{l3}[c1',c2',c3]
    //     = δ_{c1c1'}·δ_{c2c2'}
    // — completeness; the transposed relation follows since the matrix is
    // square. Range l1 ≤ 9, l2 ≤ 3 instantiates realCGDense up to l3 = 12,
    // the chain demand at k ≤ 4, lmax ≤ 3.
    let mutable worstComp = 0.0
    for l1 in 0 .. 9 do
        for l2 in 0 .. 3 do
            let d1, d2 = 2 * l1 + 1, 2 * l2 + 1
            let tables = [| for l3 in abs (l1 - l2) .. l1 + l2 -> WT.realCGDense l1 l2 l3 |]
            for c1 in 0 .. d1 - 1 do
                for c2 in 0 .. d2 - 1 do
                    for c1' in 0 .. d1 - 1 do
                        for c2' in 0 .. d2 - 1 do
                            let mutable s = 0.0
                            for t in tables do
                                let row = t.[c1].[c2]
                                let row' = t.[c1'].[c2']
                                for c3 in 0 .. row.Length - 1 do
                                    s <- s + row.[c3] * row'.[c3]
                            let expect = if c1 = c1' && c2 = c2' then 1.0 else 0.0
                            worstComp <- max worstComp (abs (s - expect))
    check "realCG completeness Σ_{l3,c3} T·T = δδ, l1 ≤ 9, l2 ≤ 3 (1e-12)"
          (worstComp < 1e-12) (sprintf "max dev %g" worstComp)

    // Exchange identity spot checks at the chain-range corners:
    // realCG(l2,l1,l3)[c2,c1,c3] = (−1)^{l1+l2−l3}·realCG(l1,l2,l3)[c1,c2,c3].
    let mutable worstEx = 0.0
    for (l1, l2) in [ (5, 2); (7, 3); (9, 3) ] do
        for l3 in abs (l1 - l2) .. l1 + l2 do
            let a = WT.realCGDense l1 l2 l3
            let b = WT.realCGDense l2 l1 l3
            let sigma = if (l1 + l2 - l3) % 2 = 0 then 1.0 else -1.0
            for c1 in 0 .. 2 * l1 do
                for c2 in 0 .. 2 * l2 do
                    for c3 in 0 .. 2 * l3 do
                        worstEx <- max worstEx (abs (b.[c2].[c1].[c3] - sigma * a.[c1].[c2].[c3]))
    check "realCG exchange identity at (5,2), (7,3), (9,3), all l3 (1e-12)"
          (worstEx < 1e-12) (sprintf "max dev %g" worstEx)

    // ========================================================================
    // Stage 2b-ii: the Sym^k LABEL layer (MLSpec.polyLabels) — counts only,
    // no emission. The two counting theorems fire inside every `polyLabels`
    // call; what is checked here is the CONVENTION (enumeration order, the
    // occurrence order shared with this module's tables) and the alignment
    // with stage 1's kept-cell enumeration at k = 2 (§6.9(v), count level).
    // ========================================================================

    let mkSpec (xs: (int * int * int) list) : MLS.Spec =
        xs |> List.map (fun (l, p, m) -> ({ L = l; Parity = p; Mult = m } : MLS.SpecEntry))

    // ---- the label layer's occurrence order IS this module's -----------------
    // MLSpec stays dependency-free (BladeML shares it) so it cannot call
    // symPowerTable; the shared convention "L descending, RREF copy ascending"
    // is therefore pinned from outside. A label's flat `Occ` index must be a
    // direct index into `tbl.Occurrences` — this is what 2b-iii will bake.
    let mutable occOrderOk = true
    let mutable occOrderWhere = ""
    for j in 1 .. 4 do
        for l in 0 .. 3 do
            let want = (symPowerTable j l).Occurrences |> List.map (fun o -> (o.L, o.Copy))
            for p in 0 .. 1 do
                let got = MLS.symOccurrences l p j
                if got <> want then
                    occOrderOk <- false
                    occOrderWhere <- sprintf "j=%d l=%d p=%d: %A vs %A" j l p got want
    check "labels: occurrence order = symPowerTable order (j ≤ 4, l ≤ 3, both parities)"
          occOrderOk occOrderWhere

    // ---- anchor A = [(0,e,1); (1,o,1)], k = 3 -------------------------------
    let anchorA = mkSpec [ (0, 0, 1); (1, 1, 1) ]
    let labA3 = MLS.polyLabels anchorA 3
    let sectorsA3 = labA3 |> List.map (fun lb -> lb.Sector) |> List.distinct
    check "anchor A k=3: 4 sectors, lex ascending over copies"
          (sectorsA3 = [ [0;0;0]; [0;0;1]; [0;1;1]; [1;1;1] ]) (sprintf "%A" sectorsA3)

    let perSectorA3 = labA3 |> List.countBy (fun lb -> lb.Sector) |> List.map snd
    check "anchor A k=3: 6 labels, per-sector 1/1/2/2"
          (labA3.Length = 6 && perSectorA3 = [ 1; 1; 2; 2 ])
          (sprintf "%d labels, per sector %A" labA3.Length perSectorA3)

    let cntA3 = MLS.polyLabelCounts anchorA 3
    check "anchor A k=3: per-(L,P) counts (0,e):2 (1,o):2 (2,e):1 (3,o):1"
          (cntA3 = Map.ofList [ ((0,0), 2); ((1,1), 2); ((2,0), 1); ((3,1), 1) ])
          (sprintf "%A" (Map.toList cntA3))

    // The full list, in order — the CONVENTION pin (occurrence choices outer,
    // copy 0 slowest; chain steps inner, last step fastest).
    let labA3Shape =
        labA3 |> List.map (fun lb ->
            (lb.Sector,
             lb.Uses |> List.map (fun u -> (u.Copy, u.Degree, u.Occ, u.OccL)),
             lb.Chain, lb.L, lb.Parity))
    let labA3Expected =
        [ ([0;0;0], [ (0, 3, 0, 0) ],                 [],  0, 0)
          ([0;0;1], [ (0, 2, 0, 0); (1, 1, 0, 1) ],   [1], 1, 1)
          ([0;1;1], [ (0, 1, 0, 0); (1, 2, 0, 2) ],   [2], 2, 0)
          ([0;1;1], [ (0, 1, 0, 0); (1, 2, 1, 0) ],   [0], 0, 0)
          ([1;1;1], [ (1, 3, 0, 3) ],                 [],  3, 1)
          ([1;1;1], [ (1, 3, 1, 1) ],                 [],  1, 1) ]
    check "anchor A k=3: full label list (enumeration-order convention pin)"
          (labA3Shape = labA3Expected) (if labA3Shape = labA3Expected then "" else sprintf "%A" labA3Shape)

    let multA3 = labA3 |> List.map (fun lb -> lb.Multinomial)
    check "anchor A k=3: sector multinomials k!/∏j_c! = 1,3,3,3,1,1"
          (multA3 = [ 1L; 3L; 3L; 3L; 1L; 1L ]) (sprintf "%A" multA3)

    // ---- k = 2: the label ↔ stage-1 kept-cell alignment (§6.9(v)) -----------
    // The correspondence is a BIJECTION labels(s,2) ↔ s2TpCells S2Sym s:
    //   mirror cell (b1 < b2, u1, u2, bo)      ↔ two-distinct-copy sector,
    //                                            copies in different blocks;
    //   diagonal cell (b, u1 < u2, bo)         ↔ two-distinct-copy sector,
    //                                            both copies in the same block
    //                                            (all L in 0..2l, i.e. both
    //                                             τ signs, are reachable);
    //   diagonal cell (b, u1 = u2, bo)         ↔ repeated-copy sector, whose
    //                                            occurrences of Sym²(V_l) are
    //                                            the l+1 even L — exactly the
    //                                            τ = +1 (σ = +1) diagonal cells.
    // Asserted at the COUNT level here (the value-level M-pin is 2b-iii).
    let k2Specs =
        [ "A [(0,e,1);(1,o,1)]", anchorA
          "B [(1,o,2)]",         mkSpec [ (1, 1, 2) ]
          "C [(0,e,2);(1,o,1)]", mkSpec [ (0, 0, 2); (1, 1, 1) ] ]
    for (nm, s) in k2Specs do
        let labs = MLS.polyLabels s 2
        let cells = MLS.s2TpCells MLS.S2Sym s
        let twoDistinct = labs |> List.filter (fun lb -> lb.Uses.Length = 2) |> List.length
        let sameCopy = labs |> List.filter (fun lb -> lb.Uses.Length = 1) |> List.length
        let mirror = cells |> List.filter (fun c -> c.IsMirror) |> List.length
        let diagOff = cells |> List.filter (fun c -> not c.IsMirror && c.OffA <> c.OffB) |> List.length
        let diagDiag = cells |> List.filter (fun c -> not c.IsMirror && c.OffA = c.OffB) |> List.length
        // Per-(L, P) alignment: a cell's output block is located by OutOff.
        let sOut = MLS.tpSpec s s
        let outAt =
            List.zip (MLS.blockStarts sOut |> List.truncate sOut.Length) sOut
            |> List.map (fun (st, e) -> (st, (e.L, e.Parity)))
            |> Map.ofList
        let cellCounts = cells |> List.countBy (fun c -> outAt.[c.OutOff]) |> Map.ofList
        let labCounts = labs |> List.countBy (fun lb -> (lb.L, lb.Parity)) |> Map.ofList
        check (sprintf "k=2 alignment %s: distinct-copy sectors = mirror + off-diag cells, repeated = u1=u2 cells" nm)
              (twoDistinct = mirror + diagOff && sameCopy = diagDiag && cellCounts = labCounts)
              (sprintf "labels %d+%d, cells %d+%d+%d, per-(L,P) %s"
                       twoDistinct sameCopy mirror diagOff diagDiag
                       (if cellCounts = labCounts then "aligned" else sprintf "%A vs %A" (Map.toList labCounts) (Map.toList cellCounts)))
        // and the stage-1 weight count, re-derived through the label layer
        let viaLabels = MLS.polyWeightDimViaLabels s 2 sOut
        let stage1 = MLS.symTpWeightDim s
        check (sprintf "k=2 weight dim %s: polyWeightDimViaLabels(s,2,tp_spec) = symTpWeightDim" nm)
              (viaLabels = stage1) (sprintf "%d vs %d" viaLabels stage1)

    // ---- the 15-spec sweep, k ∈ {2,3,4} -------------------------------------
    let sweep =
        [ mkSpec [ (0, 0, 1) ]
          mkSpec [ (1, 1, 1) ]
          mkSpec [ (2, 0, 1) ]
          mkSpec [ (3, 1, 1) ]
          mkSpec [ (0, 0, 2) ]
          mkSpec [ (1, 1, 2) ]
          mkSpec [ (2, 0, 3) ]
          mkSpec [ (1, 1, 4) ]
          mkSpec [ (0, 0, 1); (1, 1, 1) ]
          mkSpec [ (0, 0, 2); (1, 1, 1) ]
          mkSpec [ (0, 0, 1); (1, 1, 2) ]
          mkSpec [ (1, 1, 1); (2, 0, 1) ]
          mkSpec [ (1, 0, 1); (1, 1, 1) ]
          mkSpec [ (0, 0, 1); (1, 1, 1); (2, 0, 1) ]
          mkSpec [ (0, 0, 1); (1, 1, 1); (2, 0, 1); (3, 1, 1) ] ]
    let mutable sweepOk = true
    let mutable sweepBad = ""
    let mutable sweepLabels = 0
    for s in sweep do
        for k in 2 .. 4 do
            let cnt = MLS.polyLabelCounts s k
            let want =
                MLS.symPowerSpec s k |> List.map (fun e -> ((e.L, e.Parity), e.Mult)) |> Map.ofList
            let dimSum = cnt |> Map.fold (fun acc (l, _) c -> acc + c * (2 * l + 1)) 0
            let expect = int (MLS.binomial (MLS.totalDim s + k - 1) k)
            sweepLabels <- sweepLabels + (cnt |> Map.fold (fun a _ c -> a + c) 0)
            if cnt <> want || dimSum <> expect then
                sweepOk <- false
                sweepBad <- sprintf "%A k=%d: %A vs %A (dim %d vs %d)" s k (Map.toList cnt) (Map.toList want) dimSum expect
    check (sprintf "sweep: label counts = sym_spec and Σ count·(2L+1) = C(n+k−1,k), 15 specs × k ∈ {2,3,4}")
          sweepOk (if sweepOk then sprintf "%d labels total" sweepLabels else sweepBad)

    let mutable wdOk = true
    let mutable wdBad = ""
    for (nm, s) in k2Specs do
        for k in 2 .. 4 do
            for (on, sOut) in [ "s", s; "tp_spec(s,s)", MLS.tpSpec s s; "[(0,e,1)]", mkSpec [ (0, 0, 1) ] ] do
                let a = MLS.polyWeightDimViaLabels s k sOut
                let b = MLS.polyWeightDim s k sOut
                if a <> b then
                    wdOk <- false
                    wdBad <- sprintf "%s k=%d out=%s: %d vs %d" nm k on a b
    check "polyWeightDimViaLabels = polyWeightDim (3 anchors × k ∈ {2,3,4} × 3 output specs)"
          wdOk wdBad

    // ---- degenerate cases ---------------------------------------------------
    // k = 1: the labels ARE the copies (no chain, no occurrence freedom).
    let k1Ok =
        [ anchorA; mkSpec [ (0, 0, 2); (1, 1, 1) ]; mkSpec [ (1, 1, 2) ] ]
        |> List.forall (fun s ->
            let labs = MLS.polyLabels s 1
            let cps = MLS.polyCopies s
            labs.Length = cps.Length
            && List.forall2 (fun (lb: MLS.PolyLabel) (c: MLS.PolyCopy) ->
                   lb.Sector = [ c.Copy ] && lb.L = c.L && lb.Parity = c.Parity && lb.Chain = []
                   && lb.Uses.Length = 1 && lb.Uses.Head.Degree = 1 && lb.Uses.Head.Occ = 0
                   && lb.Uses.Head.OccL = c.L && lb.Multinomial = 1L) labs cps)
    check "k=1: labels = copies (L, parity, no chain), 3 specs" k1Ok ""

    // Single-copy spec: the labels are exactly the Sym⁴(V₂) occurrences.
    let sV2 = mkSpec [ (2, 0, 1) ]
    let labV2 = MLS.polyLabels sV2 4
    let v2Counts = labV2 |> List.map (fun lb -> lb.L) |> List.countBy id |> List.sortByDescending fst
    check "single-copy [(2,e,1)] k=4: labels = Sym⁴(V₂) occurrences, 8:1 6:1 5:1 4:2 2:2 0:1"
          (labV2.Length = 8
           && v2Counts = [ (8, 1); (6, 1); (5, 1); (4, 2); (2, 2); (0, 1) ]
           && labV2 |> List.forall (fun lb -> lb.Sector = [0;0;0;0] && lb.Chain = [] && lb.Parity = 0
                                              && lb.Uses.Length = 1 && lb.Uses.Head.Degree = 4))
          (sprintf "%d labels, L:mult %s" labV2.Length
                   (v2Counts |> List.map (fun (l, m) -> sprintf "%d:%d" l m) |> String.concat " "))
    check "single-copy [(2,e,1)] k=4: label order = symOccurrences order"
          ((labV2 |> List.map (fun lb -> (lb.Uses.Head.OccL, lb.Uses.Head.OccCopy))) = MLS.symOccurrences 2 0 4) ""

    // k = 0: Sym⁰ = ℝ, one label, empty sector, no used copy.
    let lab0 = MLS.polyLabels anchorA 0
    check "k=0: the single trivial label (empty sector, L=0, even)"
          (lab0.Length = 1 && lab0.Head.Sector = [] && lab0.Head.Uses = []
           && lab0.Head.Chain = [] && lab0.Head.L = 0 && lab0.Head.Parity = 0) ""

    printFooter "SymPower Tables" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "SymPower Tables"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
