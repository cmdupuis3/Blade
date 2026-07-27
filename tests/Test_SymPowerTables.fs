// Pins for the Sym^j(V_l) occurrence tables (SymPowerTables.fs) — stage 2b-i
// of plan-transforms-as-types §3.3b/§7 3b. The exact half re-verifies the
// integer/rational claims from OUTSIDE the builder (E·v = 0, occurrence
// counts vs the stage-2a weight-peel, the diagonal rational Gram); the float
// half pins global orthonormality under identity (3)'s /N_I weighting, the
// DERIVED realization phase rule (real iff j·l + L even; −i otherwise) with
// its gapped guard, and bit-pins small tables. The block also carries the
// §6.9(iii) extension: realCGDense completeness/exchange pins over the
// k ≤ 4 chain range (l1 ≤ 9, l2 ≤ 3, l3 ≤ 12).
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

    printFooter "SymPower Tables" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "SymPower Tables"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
