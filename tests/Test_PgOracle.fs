// The point-group Hom-basis COMPLETENESS oracle — stage 5b-0 of
// the retired transforms-as-types plan §3.6, held to the Test_PermOracle standard:
//
//   THERE IS NO `float` IN THIS FILE. Every matrix entry is a normalized
//   BigInteger fraction (the local `Rat` below, the PermOracle/PolyOracle
//   pattern), every comparison is structural equality of those fractions, and
//   the one number that is not exact — the stopwatch — is an int64 of
//   milliseconds printed in the footer. A pass here is an algebraic identity,
//   not a numerical one. §3.6 chose C4 and D4 precisely so that this is
//   possible: every generator entry lies in {0, ±1}, so the group average is
//   exact-rational with no field extension.
//
// ---------------------------------------------------------------------------
// WHAT IS COUNTED ELSEWHERE AND WHAT THIS FILE COVERS
// ---------------------------------------------------------------------------
// The "PG Spec" block pins the COUNT: MLPointSpec's frozen tables, their
// integrity certificate, and the FS formula Σᵢ mᵢ·nᵢ·eᵢ. A count is only a
// theorem if the basis it counts is emitted, so this block builds that basis
// explicitly — one column per (block pair, output copy, input copy, End-basis
// element) — and pins its span against a definition of Hom_G that mentions no
// label, no multiplicity and no e at all:
//
//     P_ref : Hom(V, W) → Hom(V, W),   M ↦ (1/|G|) Σ_g ρ_W(g)·M·ρ_V(g)ᵀ
//
// the group-average (Reynolds) projector onto the equivariant maps. ρ(g)ᵀ is
// ρ(g)⁻¹ because MLPointSpec.certifyPointGroup asserts every element matrix
// orthogonal — that assert is exactly the precondition of this line.
//
// Three pins per anchor, in ascending strength:
//   tr(P_ref) = pgHomDim, an INTEGER identity (this is the FS formula's
//     numerical shadow: at C4 the trace is 9 where the naive Σmᵢnᵢ says 5);
//   the emitted Gram is the §3.6 closed form d·I_e per cell, EXACTLY;
//   P_basis = B(BᵀB)⁻¹Bᵀ = P_ref ENTRYWISE over ℚ — the completeness half,
//     which is what no count and no independence certificate can see.
//
// ---------------------------------------------------------------------------
// WHY THE GRAM IS d·I_e PER CELL, AND WHY THAT IS NOT ENOUGH
// ---------------------------------------------------------------------------
// Two columns from different cells have DISJOINT SUPPORT — a cell is a fixed
// (output copy, input copy) rectangle of the dimW × dimV grid — so their inner
// product is 0 with no computation. Inside a cell the columns are the End-basis
// matrices themselves under the trace form: ⟨Id, Id⟩ = tr(Id) = d,
// ⟨J, J⟩ = tr(JᵀJ) = d (J is orthogonal), ⟨Id, J⟩ = tr(J) = 0 (J² = −Id forces
// a traceless J in 2 dimensions). Hence d·I_e, asserted entrywise below.
//
// That closed form is an INDEPENDENCE certificate and nothing more, and
// negative control (iii) is the demonstration: the spurious End column
// diag(1, −1) at C4's E has ⟨C, C⟩ = 2 = d, ⟨C, Id⟩ = 0 and ⟨C, J⟩ = 0, so it
// slots into a perfectly good d·I_3 Gram — while not being an intertwiner at
// all. Only P_ref sees it. This is the same lesson PermOracle records for its
// dropped-column control, from the opposite side.
//
// ---------------------------------------------------------------------------
// NEGATIVE CONTROLS (PermOracle discipline: each perturbation was wired into
// the LIVE pin, observed to fail loudly, reverted — and then kept as a STANDING
// assertion, so the block's discrimination is itself regression-tested)
// ---------------------------------------------------------------------------
//  (i)   DROP THE J COLUMN at C4's E. The surviving columns are still
//        independent intertwiners, the Gram is still d·I, P_basis is still an
//        honest projector — of trace exactly one less PER AFFECTED CELL, and
//        entrywise different from P_ref. This is the failure mode "the emitted
//        basis is independent but INCOMPLETE", i.e. shipping the O(3) emitter
//        unchanged against a complex-type label: 4 of the 9 parameters at the
//        C4 anchor simply would not exist. Observed: the [A×1,E×2] anchor falls
//        from 9 to 5 with 4 affected cells; the [E×1] anchor from 2 to 1.
//  (ii)  THE e ≡ 1 SIZING CONTROL. `genericHomDim` re-instantiated with
//        EndDim ≡ 1 — the naive Σ mᵢ·nᵢ — must DISAGREE with pgHomDim exactly
//        on the C4 anchors carrying an E, and AGREE everywhere else (D4, and
//        the label-disjoint zero anchor). The naive number is 5 where the
//        integer trace of P_ref is 9: visibly, arithmetically wrong.
//  (iii) A SPURIOUS End COLUMN, C = diag(1, −1), at C4's E. It commutes with
//        the identity and dies at R₉₀ (R₉₀·C ≠ C·R₉₀), so it is not an
//        intertwiner; the block reports the first group element that breaks it
//        BY WORD. Its Gram contribution is indistinguishable from a legitimate
//        one (see above), the trace rises by exactly 1, and P_basis ≠ P_ref.
//
// All three were wired into the LIVE anchor path — (i) and (iii) in place of
// the honest column list, (ii) in place of `want` — run, and reverted.
// Observed, verbatim:
//
//   (i)   8 failures, all on the two C4 anchors, none on D4 (which has no J
//         column to drop). [A×1,E×2]: "16 of 625 entries differ, first at
//         [7][7]: 0 vs 1/2 (tr 5 vs 9)"; [E×1]: "4 of 16 entries differ, first
//         at [1][1]: 0 vs 1/2 (tr 1 vs 2)". Note what did NOT fail: the
//         intertwining pin. Every surviving column is still an exact
//         intertwiner — incompleteness is invisible to independence and to
//         intertwining alike, and only P_ref sees it.
//   (ii)  the trace pin itself failed: "tr(P_ref) = pgHomDim = 5 ... tr = 9"
//         and "= 1 ... tr = 2". The naive Σ mᵢ·nᵢ is not off by a convention or
//         a normalization; it is off by whole units against an integer that was
//         computed with no reference to labels, multiplicities or e at all.
//         Every D4 anchor stayed green, which is the other half of the control:
//         the naive formula is exactly right wherever every label is real type.
//   (iii) 10 failures on the two C4 anchors. The intertwining pin fired first
//         and located it — "1 column(s) break, first at word r" — and the
//         entrywise pin gave "4 of 625 entries differ, first at [6][6]: 1 vs
//         1/2 (tr 10 vs 9)". Note what did NOT fail: the Gram closed form is
//         satisfied entry for entry by the spurious column (⟨C,C⟩ = 2 = d,
//         ⟨C,Id⟩ = ⟨C,J⟩ = 0); only the CELL SIZE (3 columns where e = 2) and
//         P_ref itself register it.
//
// Loud, specific, and off by whole units of trace rather than by an epsilon —
// the dividend of having no tolerance to hide in. The standing form of all
// three is the last family of checks at each anchor.
//
// ---------------------------------------------------------------------------
// COST
// ---------------------------------------------------------------------------
// Anchors have dim V, dim W ≤ 5, so Hom is at most 25-dimensional and the
// projectors are 25 × 25 rationals over groups of order ≤ 8. Milliseconds.
module Blade.Tests.PgOracleReview

open System.Numerics
open Blade.Tests.TestHarness
open Blade.ML.PointSpec

// ---------------------------------------------------------------------------
// Exact rationals — local, because this is an oracle: nothing it measures with
// may be code it is measuring. Always normalized (Den > 0, gcd = 1), so
// structural equality IS value equality.
// ---------------------------------------------------------------------------

type private Rat = { Num: bigint; Den: bigint }

[<RequireQualifiedAccess>]
module private Rat =
    let make (n: bigint) (d: bigint) : Rat =
        if d.IsZero then failwith "PgOracle: rational with zero denominator"
        let n, d = if d.Sign < 0 then -n, -d else n, d
        let g = BigInteger.GreatestCommonDivisor(n, d)
        if g.IsOne then { Num = n; Den = d } else { Num = n / g; Den = d / g }
    let zero : Rat = { Num = BigInteger.Zero; Den = BigInteger.One }
    let one : Rat = { Num = BigInteger.One; Den = BigInteger.One }
    let ofBigInt (n: bigint) : Rat = { Num = n; Den = BigInteger.One }
    let ofInt (n: int) : Rat = ofBigInt (bigint n)
    let isZero (a: Rat) = a.Num.IsZero
    let add (a: Rat) (b: Rat) = make (a.Num * b.Den + b.Num * a.Den) (a.Den * b.Den)
    let sub (a: Rat) (b: Rat) = make (a.Num * b.Den - b.Num * a.Den) (a.Den * b.Den)
    let mul (a: Rat) (b: Rat) = make (a.Num * b.Num) (a.Den * b.Den)
    let div (a: Rat) (b: Rat) =
        if b.Num.IsZero then failwith "PgOracle: rational division by zero"
        make (a.Num * b.Den) (a.Den * b.Num)
    let show (a: Rat) = if a.Den.IsOne then string a.Num else sprintf "%O/%O" a.Num a.Den

// ---------------------------------------------------------------------------
// The module a spec describes, as an integer matrix per group element:
// block-diagonal, one copy of the label's matrix per multiplicity copy, laid
// out at MLPointSpec.pgBlockStarts offsets — which is the layout 5b-i's emitter
// will bake, so the oracle and the emitter agree on where a cell lives.
// ---------------------------------------------------------------------------

let private repOf (grp: PointGroup) (spec: PgSpec) (el: PgElement) : int[][] =
    let d = pgTotalDim grp spec
    let m = Array.init d (fun _ -> Array.zeroCreate<int> d)
    let starts = pgBlockStarts grp spec |> List.toArray
    spec |> List.iteri (fun bi (label, mult) ->
        let ir = pgIrrep grp label
        let rho = List.item (pgIrrepIndex grp label) el.Mats
        for u in 0 .. mult - 1 do
            let off = starts.[bi] + u * ir.DimR
            for a in 0 .. ir.DimR - 1 do
                for b in 0 .. ir.DimR - 1 do
                    m.[off + a].[off + b] <- rho.[a].[b])
    m

// ---------------------------------------------------------------------------
// SIDE 1 — the reference: the Reynolds projector on Hom(V, W)
//
//   P_ref = (1/|G|) Σ_g  M ↦ ρ_W(g)·M·ρ_V(g)ᵀ
//
// as a matrix on ℝ^{dimW·dimV} with M[i][j] at index i·dimV + j:
//
//   A[(i,j)][(a,b)] = Σ_g ρ_W(g)[i][a]·ρ_V(g)[j][b],      P_ref = A / |G|.
//
// Every claim about P_ref below is a claim about the INTEGER matrix A:
//   symmetric      A[p][q] = A[q][p]        (g ↦ g⁻¹, using ρ(g⁻¹) = ρ(g)ᵀ)
//   idempotent     A·A = |G|·A              (P² = P with the denominator cleared)
//   trace          Σ_p A[p][p] = |G|·dim Hom_G
// ---------------------------------------------------------------------------

type private RefCase = {
    A: int[][]
    Order: int
    P: Rat[][]
    Trace: Rat
    Symmetric: bool
    Idempotent: bool
    IdemDetail: string
}

let private buildRef (grp: PointGroup) (sIn: PgSpec) (sOut: PgSpec) : RefCase =
    let els = groupElements grp
    let order = List.length els
    let dv = pgTotalDim grp sIn
    let dw = pgTotalDim grp sOut
    let dd = dw * dv
    let a = Array.init dd (fun _ -> Array.zeroCreate<int> dd)
    for el in els do
        let rv = repOf grp sIn el
        let rw = repOf grp sOut el
        for i in 0 .. dw - 1 do
            for p in 0 .. dw - 1 do
                if rw.[i].[p] <> 0 then
                    for j in 0 .. dv - 1 do
                        for q in 0 .. dv - 1 do
                            if rv.[j].[q] <> 0 then
                                a.[i * dv + j].[p * dv + q] <-
                                    a.[i * dv + j].[p * dv + q] + rw.[i].[p] * rv.[j].[q]

    let mutable sym = true
    for i in 0 .. dd - 1 do
        for j in 0 .. dd - 1 do
            if a.[i].[j] <> a.[j].[i] then sym <- false

    let mutable idem = true
    let mutable idemDetail = ""
    for i in 0 .. dd - 1 do
        for j in 0 .. dd - 1 do
            let mutable acc = 0L
            for k in 0 .. dd - 1 do acc <- acc + int64 a.[i].[k] * int64 a.[k].[j]
            if acc <> int64 order * int64 a.[i].[j] then
                if idem then
                    idemDetail <- sprintf "(A*A)[%d][%d] = %d but |G|*A = %d" i j acc (int64 order * int64 a.[i].[j])
                idem <- false

    let p = Array.init dd (fun i -> Array.init dd (fun j -> Rat.make (bigint a.[i].[j]) (bigint order)))
    let mutable tr = Rat.zero
    for i in 0 .. dd - 1 do tr <- Rat.add tr p.[i].[i]
    { A = a; Order = order; P = p; Trace = tr
      Symmetric = sym; Idempotent = idem; IdemDetail = idemDetail }

// ---------------------------------------------------------------------------
// SIDE 2 — the EMITTED basis and its projector B(BᵀB)⁻¹Bᵀ over ℚ
// ---------------------------------------------------------------------------

/// One emitted column: which homBlocks pair it belongs to, which cell of that
/// pair, which End-basis element, and the flattened dimW × dimV matrix.
type private Col = {
    Label: string
    Cell: int * int * int * int   // (bi, bo, uIn, uOut)
    K: int                        // index into endBasis
    Dim: int                      // d = DimR of the label (the Gram diagonal)
    Vec: int[]
}

/// The honest emitted basis: per homBlocks pair, per (u_out, u_in) cell, one
/// column per End-basis element ([Id] at e = 1, [Id, J] at e = 2). Column count
/// = Σ_pairs mOut·mIn·e = pgHomDim, which is the count/emission bridge.
let private honestColumns (grp: PointGroup) (sIn: PgSpec) (sOut: PgSpec) : Col list =
    let dv = pgTotalDim grp sIn
    let dw = pgTotalDim grp sOut
    let startsIn = pgBlockStarts grp sIn |> List.toArray
    let startsOut = pgBlockStarts grp sOut |> List.toArray
    [ for (bi, bo, (label, mOut), (_, mIn)) in pgHomBlocks grp sIn sOut do
        let ir = pgIrrep grp label
        let basis = endBasis ir |> List.toArray
        for uOut in 0 .. mOut - 1 do
          for uIn in 0 .. mIn - 1 do
            for k in 0 .. basis.Length - 1 do
              let v : int[] = Array.zeroCreate (dw * dv)
              let offO = startsOut.[bo] + uOut * ir.DimR
              let offI = startsIn.[bi] + uIn * ir.DimR
              for x in 0 .. ir.DimR - 1 do
                for y in 0 .. ir.DimR - 1 do
                  v.[(offO + x) * dv + (offI + y)] <- basis.[k].[x].[y]
              yield { Label = label; Cell = (bi, bo, uIn, uOut); K = k; Dim = ir.DimR; Vec = v } ]

/// A column built from an ARBITRARY d × d matrix at a given cell — the shape
/// negative control (iii) needs. Same placement arithmetic as the honest path,
/// so the only thing that differs is the matrix.
let private cellColumn (grp: PointGroup) (sIn: PgSpec) (sOut: PgSpec)
                       (bi: int) (bo: int) (uIn: int) (uOut: int) (mat: int[][]) : Col =
    let dv = pgTotalDim grp sIn
    let dw = pgTotalDim grp sOut
    let startsIn = pgBlockStarts grp sIn |> List.toArray
    let startsOut = pgBlockStarts grp sOut |> List.toArray
    let label = fst (List.item bi sIn)
    let ir = pgIrrep grp label
    let v : int[] = Array.zeroCreate (dw * dv)
    let offO = startsOut.[bo] + uOut * ir.DimR
    let offI = startsIn.[bi] + uIn * ir.DimR
    for x in 0 .. ir.DimR - 1 do
        for y in 0 .. ir.DimR - 1 do
            v.[(offO + x) * dv + (offI + y)] <- mat.[x].[y]
    { Label = label; Cell = (bi, bo, uIn, uOut); K = -1; Dim = ir.DimR; Vec = v }

/// The first group element at which a column fails to intertwine, by WORD:
/// ρ_W(g)·M ≠ M·ρ_V(g). `None` means the column is an exact intertwiner —
/// which every honest column must be, and the spurious control must not.
let private firstBreaker (grp: PointGroup) (sIn: PgSpec) (sOut: PgSpec) (col: Col) : string option =
    let dv = pgTotalDim grp sIn
    let dw = pgTotalDim grp sOut
    let mat = Array.init dw (fun i -> Array.init dv (fun j -> col.Vec.[i * dv + j]))
    groupElements grp
    |> List.tryPick (fun el ->
        let rv = repOf grp sIn el
        let rw = repOf grp sOut el
        let mutable ok = true
        for i in 0 .. dw - 1 do
            for j in 0 .. dv - 1 do
                let mutable l = 0
                let mutable r = 0
                for k in 0 .. dw - 1 do l <- l + rw.[i].[k] * mat.[k].[j]
                for k in 0 .. dv - 1 do r <- r + mat.[i].[k] * rv.[k].[j]
                if l <> r then ok <- false
        if ok then None else Some (wordName grp el.Word))

/// Gauss–Jordan inverse over ℚ. `None` iff singular — which never happens for
/// the Gram of independent columns, so a `None` IS a failure signal.
let private invertRat (g: bigint[][]) : Rat[][] option =
    let n = g.Length
    let a =
        Array.init n (fun i ->
            Array.init (2 * n) (fun j ->
                if j < n then Rat.ofBigInt g.[i].[j]
                elif j - n = i then Rat.one
                else Rat.zero))
    let mutable ok = true
    for col in 0 .. n - 1 do
        if ok then
            let mutable piv = -1
            for r in col .. n - 1 do
                if piv < 0 && not (Rat.isZero a.[r].[col]) then piv <- r
            if piv < 0 then ok <- false
            else
                let tmp = a.[col]
                a.[col] <- a.[piv]
                a.[piv] <- tmp
                let pv = a.[col].[col]
                for j in 0 .. 2 * n - 1 do a.[col].[j] <- Rat.div a.[col].[j] pv
                for r in 0 .. n - 1 do
                    if r <> col && not (Rat.isZero a.[r].[col]) then
                        let f = a.[r].[col]
                        for j in 0 .. 2 * n - 1 do
                            a.[r].[j] <- Rat.sub a.[r].[j] (Rat.mul f a.[col].[j])
    if ok then Some (Array.init n (fun i -> Array.sub a.[i] n n)) else None

type private BasisCase = {
    Gram: bigint[][]
    P: Rat[][]
    Trace: Rat
}

/// B(BᵀB)⁻¹Bᵀ over ℚ. The same routine serves the honest basis and every
/// control — a control differs from the real thing only in the column list
/// handed to it. An EMPTY column list is legal and gives the zero projector,
/// which is what the homDim = 0 anchor needs.
let private basisProjector (dd: int) (cols: Col[]) : BasisCase option =
    let nc = cols.Length
    if nc = 0 then
        Some { Gram = Array.empty
               P = Array.init dd (fun _ -> Array.create dd Rat.zero)
               Trace = Rat.zero }
    else
        let g =
            Array.init nc (fun p ->
                Array.init nc (fun q ->
                    let mutable acc = BigInteger.Zero
                    for t in 0 .. dd - 1 do
                        acc <- acc + bigint (cols.[p].Vec.[t] * cols.[q].Vec.[t])
                    acc))
        match invertRat g with
        | None -> None
        | Some gi ->
            // M[s][q] = Σ_p B[s][p]·Ginv[p][q], then P[s][t] = Σ_q M[s][q]·B[t][q].
            let m = Array.init dd (fun _ -> Array.create nc Rat.zero)
            for s in 0 .. dd - 1 do
                for p in 0 .. nc - 1 do
                    let v = cols.[p].Vec.[s]
                    if v <> 0 then
                        let rv = Rat.ofInt v
                        for q in 0 .. nc - 1 do
                            m.[s].[q] <- Rat.add m.[s].[q] (Rat.mul rv gi.[p].[q])
            let p = Array.init dd (fun _ -> Array.create dd Rat.zero)
            for s in 0 .. dd - 1 do
                for t in 0 .. dd - 1 do
                    let mutable acc = Rat.zero
                    for q in 0 .. nc - 1 do
                        let v = cols.[q].Vec.[t]
                        if v <> 0 && not (Rat.isZero m.[s].[q]) then
                            acc <- Rat.add acc (Rat.mul m.[s].[q] (Rat.ofInt v))
                    p.[s].[t] <- acc
            let mutable tr = Rat.zero
            for i in 0 .. dd - 1 do tr <- Rat.add tr p.[i].[i]
            Some { Gram = g; P = p; Trace = tr }

/// Entrywise comparison over ℚ — zero tolerance, structural equality of
/// normalized fractions. Returns (#differing entries, a located description of
/// the first difference in row-major order).
let private compareExact (a: Rat[][]) (b: Rat[][]) : int * string =
    let d = a.Length
    let mutable diffs = 0
    let mutable first = ""
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do
            if a.[i].[j] <> b.[i].[j] then
                if diffs = 0 then
                    first <- sprintf "first at [%d][%d]: %s vs %s" i j (Rat.show a.[i].[j]) (Rat.show b.[i].[j])
                diffs <- diffs + 1
    (diffs, first)

let private showSpec (s: PgSpec) : string =
    if List.isEmpty s then "[]"
    else s |> List.map (fun (n, m) -> sprintf "%s*%d" n m) |> String.concat "+"

/// The naive Σ mᵢ·nᵢ formula: the generic core with the FS weight forced to 1.
/// Negative control (ii) is entirely about the gap between this and pgHomDim.
let private naiveHomDim (grp: PointGroup) (sIn: PgSpec) (sOut: PgSpec) : int =
    let alg : BlockAlgebra<string> =
        { Dim = (fun n -> (pgIrrep grp n).DimR)
          EndDim = (fun _ -> 1) }
    genericHomDim alg sIn sOut

// ---------------------------------------------------------------------------

type private Anchor = {
    Tag: string
    Grp: PointGroup
    SIn: PgSpec
    SOut: PgSpec
}

let runPgOracleTests () : BlockResult =
    printHeader "PG Oracle (the emitted [Id, J] basis vs the exact Reynolds projector)"
    let sw = System.Diagnostics.Stopwatch.StartNew()
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

    let c4 = pointGroup "C4"
    let d4 = pointGroup "D4"

    let anchors =
        [ { Tag = "C4"; Grp = c4; SIn = [ ("A", 1); ("E", 2) ]; SOut = [ ("A", 1); ("E", 2) ] }
          { Tag = "C4"; Grp = c4; SIn = [ ("E", 1) ]; SOut = [ ("E", 1) ] }
          { Tag = "D4"; Grp = d4; SIn = [ ("A1", 1); ("E", 2) ]; SOut = [ ("A1", 1); ("E", 2) ] }
          { Tag = "D4"; Grp = d4; SIn = [ ("E", 2) ]; SOut = [ ("B1", 1); ("E", 1) ] }
          // The ZERO anchor: label-disjoint, so Hom_G is {0} and the emitted
          // basis is empty. P_ref must be the zero matrix, not merely small.
          { Tag = "C4"; Grp = c4; SIn = [ ("B", 1) ]; SOut = [ ("A", 1) ] } ]

    for anc in anchors do
        let grp = anc.Grp
        let tag = sprintf "%s %s -> %s" anc.Tag (showSpec anc.SIn) (showSpec anc.SOut)
        let dv = pgTotalDim grp anc.SIn
        let dw = pgTotalDim grp anc.SOut
        let dd = dw * dv
        let want = pgHomDim grp anc.SIn anc.SOut

        // ====================================================================
        // 1. THE REFERENCE PROJECTOR — exact, and about group elements only
        // ====================================================================
        let rf = buildRef grp anc.SIn anc.SOut
        check (sprintf "%s: tr(P_ref) = pgHomDim = %d, an INTEGER (the FS formula's numerical shadow)" tag want)
              (rf.Trace = Rat.ofInt want && rf.Trace.Den.IsOne)
              (sprintf "tr = %s, dim V = %d, dim W = %d, dim Hom = %d, |G| = %d"
                   (Rat.show rf.Trace) dv dw dd rf.Order)
        check (sprintf "%s: P_ref idempotent and symmetric over Q (exact)" tag)
              (rf.Idempotent && rf.Symmetric)
              (if not rf.Idempotent then rf.IdemDetail
               elif not rf.Symmetric then "A is not symmetric"
               else sprintf "P^2 = P via A*A = %d*A, and A = A^T, over %d^2 entries" rf.Order dd)

        // ====================================================================
        // 2. THE EMITTED BASIS — every column is an exact intertwiner
        // ====================================================================
        let cols = honestColumns grp anc.SIn anc.SOut |> List.toArray
        check (sprintf "%s: the emitted basis has exactly pgHomDim = %d columns (sum over cells of e)" tag want)
              (cols.Length = want)
              (sprintf "%d columns over %d homBlocks pair(s); cells carry e = %s"
                   cols.Length (List.length (pgHomBlocks grp anc.SIn anc.SOut))
                   (pgHomBlocks grp anc.SIn anc.SOut
                    |> List.map (fun (_, _, (l, _), _) -> sprintf "%s:%d" l (endDim (pgIrrep grp l).Fs))
                    |> String.concat " "))
        let breakers = cols |> Array.choose (fun c -> firstBreaker grp anc.SIn anc.SOut c)
        check (sprintf "%s: every emitted column intertwines EXACTLY at every group element (integer)" tag)
              (breakers.Length = 0)
              (if breakers.Length = 0 then sprintf "%d columns x %d elements, all exact" cols.Length rf.Order
               else sprintf "%d column(s) break, first at word %s" breakers.Length breakers.[0])

        // ====================================================================
        // 3. THE GRAM CLOSED FORM — d*I_e per cell, entrywise
        // ====================================================================
        match basisProjector dd cols with
        | None ->
            check (sprintf "%s: Gram (B^T B) is invertible over Q" tag) false
                  "Gaussian elimination found a zero pivot - the emitted basis is dependent"
        | Some bs ->
            let mutable gramOk = true
            let mutable gramBad = ""
            for p in 0 .. cols.Length - 1 do
                for q in 0 .. cols.Length - 1 do
                    let predicted =
                        if p = q then bigint cols.[p].Dim
                        elif cols.[p].Cell = cols.[q].Cell then BigInteger.Zero  // <Id, J> = tr J = 0
                        else BigInteger.Zero                                     // disjoint support
                    if bs.Gram.[p].[q] <> predicted then
                        if gramOk then
                            gramBad <- sprintf "[%d][%d] = %O but the closed form says %O" p q bs.Gram.[p].[q] predicted
                        gramOk <- false
            // ... and the cells really do carry e columns apiece.
            let cellSizes = cols |> Array.toList |> List.groupBy (fun c -> c.Cell)
            let sizesOk =
                cellSizes |> List.forall (fun (_, cs) ->
                    List.length cs = endDim (pgIrrep grp (List.head cs).Label).Fs)
            check (sprintf "%s: Gram = d*I_e per cell EXACTLY (%d cells, %d^2 entries)" tag (List.length cellSizes) cols.Length)
                  (gramOk && sizesOk)
                  (if not gramOk then gramBad
                   elif not sizesOk then "a cell does not carry e columns"
                   else sprintf "block-diagonal, diagonal entries %s"
                            (cols |> Array.map (fun c -> string c.Dim) |> Array.distinct |> String.concat "/"))

            // ================================================================
            // 4. THE PIN: P_basis = P_ref ENTRYWISE OVER Q
            // ================================================================
            let (diffs, firstDiff) = compareExact bs.P rf.P
            check (sprintf "%s: B(B^T B)^-1 B^T = P_ref ENTRYWISE over Q, %d^2 entries, zero tolerance" tag dd)
                  (diffs = 0 && bs.Trace = rf.Trace)
                  (if diffs = 0 then sprintf "identical; tr = %s on both sides" (Rat.show bs.Trace)
                   else sprintf "%d of %d entries differ, %s (tr %s vs %s)"
                            diffs (dd * dd) firstDiff (Rat.show bs.Trace) (Rat.show rf.Trace))
            if want = 0 then
                let zeroOk =
                    rf.P |> Array.forall (Array.forall (fun v -> v = Rat.zero))
                check (sprintf "%s: the ZERO anchor — P_ref is the zero matrix and the emitted basis is empty" tag)
                      (zeroOk && cols.Length = 0 && diffs = 0)
                      (sprintf "%d columns, tr = %s" cols.Length (Rat.show rf.Trace))

            // ================================================================
            // 5. NEGATIVE CONTROL (i) — DROP THE J COLUMNS
            // ================================================================
            let jCols = cols |> Array.filter (fun c -> c.K > 0)
            if jCols.Length > 0 then
                let kept = cols |> Array.filter (fun c -> c.K = 0)
                let affectedCells = jCols |> Array.map (fun c -> c.Cell) |> Array.distinct |> Array.length
                match basisProjector dd kept with
                | None ->
                    check (sprintf "%s: NC(i) dropping the J columns -> trace deficit and P_basis <> P_ref" tag)
                          false "the reduced Gram was singular - control inconclusive"
                | Some nb ->
                    let (nd, nfirst) = compareExact nb.P rf.P
                    let trOk = nb.Trace = Rat.ofInt (want - affectedCells)
                    check (sprintf "%s: NC(i) drop J -> tr falls by exactly 1 per affected cell (%d -> %d) and P_basis <> P_ref"
                               tag want (want - affectedCells))
                          (trOk && nd > 0 && kept.Length = want - affectedCells)
                          (sprintf "%d affected cell(s); tr = %s (want %d); %d of %d entries differ, %s"
                               affectedCells (Rat.show nb.Trace) (want - affectedCells) nd (dd * dd) nfirst)

            // ================================================================
            // 6. NEGATIVE CONTROL (iii) — A SPURIOUS End COLUMN, diag(1, -1)
            // ================================================================
            // Only where the group is C4 and the cell's label is the E of a
            // complex-type pair: that is the cell where the emitter has a real
            // choice to get wrong, and diag(1, -1) is the plausible wrong one
            // (it IS an endomorphism of the underlying vector space, and it
            // even satisfies the Gram closed form).
            let eCell =
                pgHomBlocks grp anc.SIn anc.SOut
                |> List.tryPick (fun (bi, bo, (l, _), _) ->
                    if grp.Name = "C4" && (pgIrrep grp l).DimR = 2 then Some (bi, bo) else None)
            match eCell with
            | None -> ()
            | Some (bi, bo) ->
                let spur = cellColumn grp anc.SIn anc.SOut bi bo 0 0 [| [| 1; 0 |]; [| 0; -1 |] |]
                let breaker = firstBreaker grp anc.SIn anc.SOut spur
                check (sprintf "%s: NC(iii) the spurious End column diag(1,-1) is NOT an intertwiner — it dies at a named element" tag)
                      (breaker = Some "r")
                      (sprintf "first breaking word = %s (R90 does not commute with diag(1,-1))"
                           (match breaker with Some w -> w | None -> "<none: the control did not fire>"))
                let extended = Array.append cols [| spur |]
                match basisProjector dd extended with
                | None ->
                    check (sprintf "%s: NC(iii) spurious column -> trace rises by 1 and P_basis <> P_ref" tag)
                          false "the extended Gram was singular - control inconclusive"
                | Some nb ->
                    let (nd, nfirst) = compareExact nb.P rf.P
                    let n = cols.Length
                    // The Gram is BLIND to it: <C,C> = d, <C,Id> = tr C = 0,
                    // <C,J> = tr(C^T J) = 0. Only P_ref sees the difference.
                    let gramBlind =
                        nb.Gram.[n].[n] = bigint spur.Dim
                        && [ 0 .. n - 1 ] |> List.forall (fun p ->
                               nb.Gram.[n].[p] = BigInteger.Zero && nb.Gram.[p].[n] = BigInteger.Zero)
                    check (sprintf "%s: NC(iii) tr rises to %d, P_basis <> P_ref — while the Gram closed form stays BLIND" tag (want + 1))
                          (nb.Trace = Rat.ofInt (want + 1) && nd > 0 && gramBlind)
                          (sprintf "tr = %s (want %d); Gram[%d][%d] = %O = d with zero off-diagonal (indistinguishable from an honest column); %d of %d entries differ, %s"
                               (Rat.show nb.Trace) (want + 1) n n nb.Gram.[n].[n] nd (dd * dd) nfirst)

        // ====================================================================
        // 7. NEGATIVE CONTROL (ii) — THE e = 1 SIZING CONTROL
        // ====================================================================
        // The naive Sum m_i*n_i must be WRONG exactly where an FS-complex label
        // appears, and RIGHT everywhere else. tr(P_ref) is the arbiter: it is
        // an integer computed with no reference to labels at all.
        let naive = naiveHomDim grp anc.SIn anc.SOut
        let hasComplex =
            pgHomBlocks grp anc.SIn anc.SOut
            |> List.exists (fun (_, _, (l, _), _) -> endDim (pgIrrep grp l).Fs > 1)
        if hasComplex then
            check (sprintf "%s: NC(ii) the naive sum m_i*n_i says %d, but tr(P_ref) = %d — the FS correction is not optional" tag naive want)
                  (naive <> want && rf.Trace = Rat.ofInt want)
                  (sprintf "naive %d vs FS-weighted %d vs exact trace %s" naive want (Rat.show rf.Trace))
        else
            check (sprintf "%s: NC(ii) control is SILENT here — every label is of real type, so naive = FS = %d" tag want)
                  (naive = want && rf.Trace = Rat.ofInt want)
                  (sprintf "naive %d = FS-weighted %d = exact trace %s" naive want (Rat.show rf.Trace))

    sw.Stop()
    printFooter "PG Oracle"
        [ sprintf "%d passed" passed; sprintf "%d failed" failed; sprintf "%d ms" sw.ElapsedMilliseconds ]
    { Block = "PG Oracle"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
