// The Sym^k label-basis ORACLE — stage 2b-iii of plan-transforms-as-types
// (§3.3b's evaluation identity, §7 3b's projector-equality bullet, §6.9(v)'s
// alignment-table requirement). Deliberately INDEPENDENT of the 2b-i/2b-ii
// pipeline on the reference side: nothing here calls SymPowerTables' exact
// layer (symPowerExact / applyE / applyF / gramDot / symCells / Rat) — those
// are the code under test. The sl₂ action, the Casimir, the exact rationals
// and the monomial↔tensor scalings are all re-derived locally from the
// conventions, so a convention drift in either file fails loudly here.
//
// TWO DELIVERABLES.
//
// 1. PROJECTOR EQUALITY (the completeness oracle). Per anchor (spec, k) and
//    per (L, parity):
//      side 1 — assemble every label's FULL 2L+1-dimensional copy in real
//        monomial coordinates from the SHIPPED pipeline (MLSpec.polyLabels +
//        SymPowerTables.symPowerTable rows + WignerTables.realCGDense chains +
//        the √(k!/∏j_c!) sector constant), per §3.3b identities (1)/(2)/(4),
//        and form P_conv = Σ_labels Σ_M v vᵀ;
//      side 2 — the isotypic projector by CASIMIR–LAGRANGE interpolation
//        (SVD-free, convention-free) over EXACT integers/rationals on the
//        divided-power monomial basis, conjugated into the same real frame.
//    Pinned: ‖P_ref − P_conv‖_max < 1e-10, plus the exact-side integer trace
//    tr(P_ref) = (2L+1)·mult BEFORE any float exists, plus P_ref ≡ 0 at every
//    (L, P) the weight-peel says is absent.
//
// 2. THE k = 2 M-PIN (§6.9(v), lifted from 2b-ii's count level to the value
//    level). Stage 1's packed slots are embedded through MLSpec.symTpEmbedTable
//    into the dense derive_tp coefficient space and evaluated at x = y; the
//    label↔kept-cell alignment table is EMITTED from the three-row rule and
//    asserted as a bijection; then the full change-of-basis matrix M is pinned
//    to be a label-aligned scaled permutation — aligned blocks ratio·I with
//    |ratio| in the DERIVED closed-form set, every cross pair 0.
//
// ---------------------------------------------------------------------------
// THE COORDINATE FRAME (stated once; everything below lives in it)
// ---------------------------------------------------------------------------
// V has the spec's orthonormal real basis e_0..e_{n−1} (block-major,
// multiplicity inner, real component innermost). Sym^k(V) ⊂ V^⊗k carries the
// induced inner product; for a lex `symMultisets` cell I the arrangement sum
// s_I = Σ_{N_I arrangements} e⊗..⊗e has ⟨s_I, s_J⟩ = δ·N_I, N_I = k!/∏α_i!.
// So {ŝ_I = s_I/√N_I} is an ORTHONORMAL frame of Sym^k(V), and §3.3b's table
// entry T[label, I] = ⟨v_label, s_I⟩ has frame coordinate T[label, I]/√N_I —
// under which identity (3)'s Σ_I T·T/N_I = δ is a plain dot product. Every
// vector and every projector in this file is written in that frame.
module Blade.Tests.PolyOracleReview

open System.Numerics
open System.Collections.Generic
open Blade.Tests.TestHarness

module MLS = Blade.ML.Spec
module SPT = Blade.ML.SymPowerTables
module WT = Blade.ML.WignerTables

// ---------------------------------------------------------------------------
// Small combinatorial utilities (local — SymPowerTables' are under test)
// ---------------------------------------------------------------------------

let private factB (m: int) : bigint =
    let mutable a = BigInteger.One
    for i in 2 .. m do a <- a * bigint i
    a

let private factF (m: int) : float =
    let mutable a = 1.0
    for i in 2 .. m do a <- a * float i
    a

let rec private removeFirst (x: int) (xs: int list) =
    match xs with
    | [] -> []
    | h :: t -> if h = x then t else h :: removeFirst x t

/// Distinct ordered arrangements of a sorted multiset (k ≤ 4).
let rec private arrangements (xs: int list) : int list list =
    match xs with
    | [] -> [ [] ]
    | _ ->
        xs
        |> List.distinct
        |> List.collect (fun x -> arrangements (removeFirst x xs) |> List.map (fun r -> x :: r))

/// N_I = k!/∏α_i! — the number of distinct arrangements of a cell.
let private cellN (k: int) (cell: int list) : int =
    let d = cell |> List.countBy id |> List.fold (fun acc (_, a) -> acc * factB a) BigInteger.One
    int (factB k / d)

/// ∏_i α_i! of a cell.
let private cellAlphaFact (cell: int list) : float =
    cell |> List.countBy id |> List.fold (fun acc (_, a) -> acc * factF a) 1.0

/// Exact integer matrix product (the Casimir/Lagrange arithmetic).
let private matMulB (a: bigint[][]) (b: bigint[][]) : bigint[][] =
    let m = if b.Length = 0 then 0 else b.[0].Length
    Array.init a.Length (fun i ->
        let ai = a.[i]
        let row = Array.create m BigInteger.Zero
        for t in 0 .. b.Length - 1 do
            let v = ai.[t]
            if not v.IsZero then
                let bt = b.[t]
                for j in 0 .. m - 1 do
                    if not bt.[j].IsZero then row.[j] <- row.[j] + v * bt.[j]
        row)

// ---------------------------------------------------------------------------
// The copy layout: a spec copy's global offset in the real basis
// ---------------------------------------------------------------------------

type private CopyInfo = { Copy: int; L: int; Parity: int; Off: int }

let private copyInfos (s: MLS.Spec) : CopyInfo[] =
    let starts = MLS.blockStarts s |> List.toArray
    MLS.polyCopies s
    |> List.map (fun c ->
        { Copy = c.Copy; L = c.L; Parity = c.Parity
          Off = starts.[c.Block] + c.MultIdx * (2 * c.L + 1) })
    |> List.toArray

// ---------------------------------------------------------------------------
// SIDE 1 — the convention under test, assembled as polynomials in x
//
// §3.3b identity (2): feature_label(x) = Σ_I T[label, I]·∏_{j∈I} x[j], and
// identity (4): feature_label = √(k!/∏j_c!) · [CG-chain of per-copy features].
// So T[label, ·] is read off as the coefficient list of an honest degree-k
// polynomial — no symmetrizer, no N_I bookkeeping in evaluation.
// ---------------------------------------------------------------------------

/// A polynomial in x[0..n−1]: sorted cell (multiset of indices) → coefficient.
type private PolyV = Map<int list, float>

let private polyAddMul (acc: PolyV) (scale: float) (a: PolyV) (b: PolyV) : PolyV =
    let mutable m = acc
    for KeyValue (ka, va) in a do
        for KeyValue (kb, vb) in b do
            let key = List.sort (ka @ kb)
            let v = scale * va * vb
            m <- m |> Map.change key (fun cur -> Some (defaultArg cur 0.0 + v))
    m

/// One CG coupling step of the left-comb chain, over the FULL real block
/// (all output components, not one row).
let private couple (l1: int) (l2: int) (l3: int) (a: PolyV[]) (b: PolyV[]) : PolyV[] =
    let cg = WT.realCGDense l1 l2 l3
    Array.init (2 * l3 + 1) (fun c3 ->
        let mutable acc : PolyV = Map.empty
        for c1 in 0 .. 2 * l1 do
            for c2 in 0 .. 2 * l2 do
                let v = cg.[c1].[c2].[c3]
                if v <> 0.0 then acc <- polyAddMul acc v a.[c1] b.[c2]
        acc)

/// The per-copy feature of one used copy: the T_{j,l} occurrence rows read as
/// polynomials in that copy's slice of x (local cell + copy offset).
let private copyFeature (u: MLS.PolyCopyUse) (off: int) : PolyV[] =
    let tbl = SPT.symPowerTable u.Degree u.CopyL
    let occ = tbl.Occurrences |> List.item u.Occ
    if occ.L <> u.OccL || occ.Copy <> u.OccCopy then
        failwithf "PolyOracle: occurrence-order drift (label says L=%d copy=%d, table says L=%d copy=%d)"
            u.OccL u.OccCopy occ.L occ.Copy
    Array.init (2 * occ.L + 1) (fun c ->
        let mutable m : PolyV = Map.empty
        tbl.Cells |> Array.iteri (fun ci cell ->
            let v = occ.Rows.[c].[ci]
            if v <> 0.0 then m <- Map.add (cell |> List.map (fun i -> i + off)) v m)
        m)

/// v_label as 2L+1 polynomials — §3.3b identity (1)'s vector, evaluated
/// through identity (4). The sector constant √(k!/∏j_c!) appears here and
/// only here (`PolyLabel.Multinomial` is the integer k!/∏j_c!).
let private labelPoly (cis: CopyInfo[]) (lb: MLS.PolyLabel) : PolyV[] =
    match lb.Uses with
    | [] -> [| Map.ofList [ ([], 1.0) ] |]
    | u0 :: rest ->
        let mutable accL = u0.OccL
        let mutable acc = copyFeature u0 cis.[u0.Copy].Off
        let mutable ch = lb.Chain
        for u in rest do
            let mid = List.head ch
            ch <- List.tail ch
            acc <- couple accL u.OccL mid acc (copyFeature u cis.[u.Copy].Off)
            accL <- mid
        if accL <> lb.L then
            failwithf "PolyOracle: chain ended at L=%d but the label says L=%d" accL lb.L
        let sc = sqrt (float lb.Multinomial)
        acc |> Array.map (Map.map (fun _ v -> v * sc))

/// Polynomial coefficients → orthonormal-frame coordinates (T[I]/√N_I).
let private toFrame (k: int) (cells: int list []) (cidx: Map<int list, int>) (p: PolyV) : float[] =
    let y = Array.zeroCreate cells.Length
    for KeyValue (key, v) in p do
        match Map.tryFind key cidx with
        | Some i -> y.[i] <- v / sqrt (float (cellN k cells.[i]))
        | None -> failwithf "PolyOracle: monomial %A is not a Sym^%d cell" key k
    y

// ---------------------------------------------------------------------------
// SIDE 2 — the independent reference: Casimir–Lagrange isotypic projectors
//
// The divided-power weight basis w_μ of each copy (w_μ = c_μ·|l,μ⟩ with
// c_μ = √((l+μ)!(l−μ)!/(2l)!), positive and mirror-symmetric) makes the sl₂
// generators INTEGER:  E·w_μ = (l−μ)w_{μ+1},  F·w_μ = (l+μ)w_{μ−1},
// H·w_μ = 2μ·w_μ  ([E,F] = H checks out: (l+μ)(l−μ+1) − (l−μ)(l+μ+1) = 2μ).
// Extended to degree-k monomials as DERIVATIONS this is the honest Sym^k
// action, so the Casimir C = FE + H(H+2)/4 is, on a monomial of weight
// w = Σμ, the integer matrix  C = F·E + w(w+1)·I  with eigenvalue L(L+1).
// The isotypic projector is then pure Lagrange interpolation
//   P_L = ∏_{L'≠L} (C − L'(L'+1)I)/(L(L+1) − L'(L'+1))
// over L' = 0 .. k·lmax — a SUPERSET of the occurring spectrum, which is
// harmless (p(c_L)=1, p(c_{L'})=0 still selects the L-eigenspace) and keeps
// the reference free of any dependence on the weight-peel. Exactness: the
// numerator stays an integer matrix and the denominator an integer, so the
// trace check tr(P) = (2L+1)·mult is an exact integer division.
//
// Parity is NOT in sl₂, so the space is split by parity FIRST: a monomial's
// parity is Σ_a p_{copy(a)} mod 2 (§3.3b — O(3) parity acts on Sym^j(V_{l,p})
// by (−1)^{j·p}), and E/F preserve each factor's copy hence the parity.
//
// FRAME CONVERSION. The monomial↔tensor identification is mono_A ↦ (∏α!)·s_A
// (the unique scaling under which the derivation action equals Σ_a 1⊗..E..⊗1;
// check at k=2: X·w₀² = 2w₀w₁ ↦ 2s_{01} and X_tensor(2s_{00}) = 2s_{01} ✓).
// Composing with w = c·|l,μ⟩ and s_A = √N_A·ŝ_A gives the positive diagonal
//   D_A = (∏α_A!)·(∏_a c_{μ_a})·√(N_A)
// from w-monomial coordinates to the orthonormal COMPLEX cell frame; the
// unitary G[I][A] = ⟨ŝ^real_I, ŝ^complex_A⟩ then rotates to the real frame.
// Hence P_real-frame = (G·D)·P_monomial·(G·D)⁻¹.
// (G is built from uMatrix — the shared frame. Note the oracle is INSENSITIVE
// to the u-vs-conj(u) reading of that matrix: swapping them replaces G by
// conj(G), and since the middle factor is real the result is conj(P) = P.)
// ---------------------------------------------------------------------------

type private RefCase = {
    Cells: int list []
    CellIdx: Map<int list, int>
    /// P_ref per (L, parity) in the orthonormal real-cell frame
    Proj: Dictionary<int * int, float[][]>
    TraceOk: bool
    TraceDetail: string
    /// every (L, P) absent from sym_spec has an identically-zero numerator
    ZeroOk: bool
    ZeroDetail: string
    MaxImag: float
    /// ‖G·G† − I‖_max — the frame transform's own unitarity
    UnitaryDev: float
}

let private buildRef (s: MLS.Spec) (k: int) : RefCase =
    let n = MLS.totalDim s
    let cis = copyInfos s
    let cellsL = MLS.symMultisets n k |> List.toArray
    let cidx = cellsL |> Array.mapi (fun i c -> (c, i)) |> Map.ofArray
    let nC = cellsL.Length
    let cells = cellsL |> Array.map List.toArray

    // per global index: copy, l, local index a = μ + l, parity
    let copyOf = Array.zeroCreate n
    let lOf = Array.zeroCreate n
    let aOf = Array.zeroCreate n
    let parOf = Array.zeroCreate n
    for ci in cis do
        for a in 0 .. 2 * ci.L do
            copyOf.[ci.Off + a] <- ci.Copy
            lOf.[ci.Off + a] <- ci.L
            aOf.[ci.Off + a] <- a
            parOf.[ci.Off + a] <- ci.Parity
    let cellPar = cells |> Array.map (fun c -> (c |> Array.sumBy (fun i -> parOf.[i])) % 2)
    let cellW = cells |> Array.map (fun c -> c |> Array.sumBy (fun i -> aOf.[i] - lOf.[i]))

    // the derivation action, as (target cell, integer coefficient) pairs
    let act (raising: bool) (ci: int) : (int * int) list =
        let cellList = List.ofArray cells.[ci]
        cellList
        |> List.countBy id
        |> List.choose (fun (i, alpha) ->
            if raising then
                if aOf.[i] < 2 * lOf.[i] then
                    Some (cidx.[List.sort ((i + 1) :: removeFirst i cellList)],
                          alpha * (2 * lOf.[i] - aOf.[i]))
                else None
            else
                if aOf.[i] > 0 then
                    Some (cidx.[List.sort ((i - 1) :: removeFirst i cellList)],
                          alpha * aOf.[i])
                else None)

    // ---- the frame transform G (unitary) and the scaling D (positive) -----
    let uCache = Dictionary<int, Complex[][]>()
    let uM (l: int) =
        match uCache.TryGetValue l with
        | true, v -> v
        | _ -> let v = WT.uMatrix l in uCache.[l] <- v; v
    let uEntry (i: int) (j: int) : Complex =
        if copyOf.[i] <> copyOf.[j] then Complex.Zero
        else (uM lOf.[i]).[aOf.[i]].[aOf.[j]]
    let nArr = cellsL |> Array.map (cellN k)
    let arrsOf = cellsL |> Array.map (fun c -> arrangements c |> List.map List.toArray |> List.toArray)
    // G[I][A] = ⟨ŝ^R_I, ŝ^C_A⟩ = √(N_I/N_A)·Σ_{arr(A)} ∏_a ⟨e_{I_a}, f_{A_a}⟩
    // (the arrangement sum over I contributes the factor N_I by symmetry).
    let g = Array.init nC (fun _ -> Array.zeroCreate<Complex> nC)
    for iI in 0 .. nC - 1 do
        let arrI = cells.[iI]
        for iA in 0 .. nC - 1 do
            let mutable acc = Complex.Zero
            for arrA in arrsOf.[iA] do
                let mutable pr = Complex.One
                let mutable a = 0
                while a < k && pr <> Complex.Zero do
                    pr <- pr * uEntry arrI.[a] arrA.[a]
                    a <- a + 1
                acc <- acc + pr
            if acc <> Complex.Zero then
                g.[iI].[iA] <- acc * Complex(sqrt (float nArr.[iI] / float nArr.[iA]), 0.0)
    let mutable udev = 0.0
    for i in 0 .. nC - 1 do
        for j in 0 .. nC - 1 do
            let mutable acc = Complex.Zero
            for a in 0 .. nC - 1 do acc <- acc + g.[i].[a] * Complex.Conjugate g.[j].[a]
            udev <- max udev (Complex.Abs (acc - (if i = j then Complex.One else Complex.Zero)))

    let cCoef (i: int) =
        sqrt (float (factB aOf.[i]) * float (factB (2 * lOf.[i] - aOf.[i])) / float (factB (2 * lOf.[i])))
    let dDiag =
        cellsL |> Array.mapi (fun idx c ->
            cellAlphaFact c * (c |> List.fold (fun acc i -> acc * cCoef i) 1.0) * sqrt (float nArr.[idx]))

    // ---- Casimir–Lagrange, per parity sector ------------------------------
    let expected = MLS.symPowerSpec s k |> List.map (fun e -> ((e.L, e.Parity), e.Mult)) |> Map.ofList
    let lmaxK = k * (cis |> Array.fold (fun a c -> max a c.L) 0)
    let projs = Dictionary<int * int, float[][]>()
    let mutable traceOk = true
    let mutable traceDetail = ""
    let mutable zeroOk = true
    let mutable zeroDetail = ""
    let mutable maxImag = 0.0

    for par in 0 .. 1 do
        let idxs = [| for i in 0 .. nC - 1 do if cellPar.[i] = par then yield i |]
        if idxs.Length > 0 then
            let pos = idxs |> Array.mapi (fun p i -> (i, p)) |> Map.ofArray
            let m = idxs.Length
            let emat = Array.init m (fun _ -> Array.create m BigInteger.Zero)
            let fmat = Array.init m (fun _ -> Array.create m BigInteger.Zero)
            idxs |> Array.iteri (fun c ci ->
                for (tgt, coef) in act true ci do
                    emat.[pos.[tgt]].[c] <- emat.[pos.[tgt]].[c] + bigint coef
                for (tgt, coef) in act false ci do
                    fmat.[pos.[tgt]].[c] <- fmat.[pos.[tgt]].[c] + bigint coef)
            let fe = matMulB fmat emat
            let cas =
                Array.init m (fun i ->
                    Array.init m (fun j ->
                        if i = j then fe.[i].[j] + bigint (cellW.[idxs.[i]] * (cellW.[idxs.[i]] + 1))
                        else fe.[i].[j]))
            for L in 0 .. lmaxK do
                let mutable num =
                    Array.init m (fun i -> Array.init m (fun j -> if i = j then BigInteger.One else BigInteger.Zero))
                let mutable den = BigInteger.One
                for l2 in 0 .. lmaxK do
                    if l2 <> L then
                        let shift = bigint (l2 * (l2 + 1))
                        let sh =
                            Array.init m (fun i ->
                                Array.init m (fun j -> if i = j then cas.[i].[j] - shift else cas.[i].[j]))
                        num <- matMulB num sh
                        den <- den * bigint (L * (L + 1) - l2 * (l2 + 1))
                let mult = defaultArg (Map.tryFind (L, par) expected) 0
                if mult = 0 then
                    if num |> Array.exists (Array.exists (fun (v: bigint) -> not v.IsZero)) then
                        zeroOk <- false
                        zeroDetail <- sprintf "L=%d P=%d numerator nonzero" L par
                else
                    let mutable tr = BigInteger.Zero
                    for i in 0 .. m - 1 do tr <- tr + num.[i].[i]
                    if not (BigInteger.Remainder(tr, den)).IsZero
                       || BigInteger.Divide(tr, den) <> bigint ((2 * L + 1) * mult) then
                        traceOk <- false
                        traceDetail <- sprintf "L=%d P=%d: tr = %O/%O, want %d" L par tr den ((2 * L + 1) * mult)
                    // D·P·D⁻¹ scattered onto the full cell space, then G(·)G†
                    let scale = 1.0 / float den
                    let m1 = Array.init nC (fun _ -> Array.zeroCreate<float> nC)
                    for r in 0 .. m - 1 do
                        for c in 0 .. m - 1 do
                            if not num.[r].[c].IsZero then
                                let ir = idxs.[r]
                                let ic = idxs.[c]
                                m1.[ir].[ic] <- float num.[r].[c] * scale * dDiag.[ir] / dDiag.[ic]
                    let t = Array.init nC (fun _ -> Array.zeroCreate<Complex> nC)
                    for i in 0 .. nC - 1 do
                        for a in 0 .. nC - 1 do
                            let gv = g.[i].[a]
                            if gv <> Complex.Zero then
                                let row = m1.[a]
                                for j in 0 .. nC - 1 do
                                    if row.[j] <> 0.0 then t.[i].[j] <- t.[i].[j] + gv * Complex(row.[j], 0.0)
                    let pr = Array.init nC (fun _ -> Array.zeroCreate<float> nC)
                    for i in 0 .. nC - 1 do
                        for j in 0 .. nC - 1 do
                            let mutable acc = Complex.Zero
                            for b in 0 .. nC - 1 do
                                let tv = t.[i].[b]
                                if tv <> Complex.Zero then acc <- acc + tv * Complex.Conjugate g.[j].[b]
                            maxImag <- max maxImag (abs acc.Imaginary)
                            pr.[i].[j] <- acc.Real
                    projs.[(L, par)] <- pr

    { Cells = cellsL; CellIdx = cidx; Proj = projs
      TraceOk = traceOk; TraceDetail = traceDetail
      ZeroOk = zeroOk; ZeroDetail = zeroDetail
      MaxImag = maxImag; UnitaryDev = udev }

// ---------------------------------------------------------------------------
// The k = 2 M-pin: stage 1's packed slots as vectors of Sym²(V)
// ---------------------------------------------------------------------------

/// Evaluate the DENSE `derive_tp` coefficient tensor at x = y: per (output
/// block, output multiplicity index), the 2L+1 quadratic forms in x. This is
/// tpDecl's baked layout (`pWOff(p) + (mo*m1 + u1)*m2 + u2`) recomputed here —
/// the layout is the contract stage 1a's embed table is written against.
let private denseQuadratic (s: MLS.Spec) (d: float[]) : Dictionary<int * int, PolyV[]> =
    let cfg = MLS.selfTpConfig s
    let paths = MLS.tpPaths cfg |> List.toArray
    let sA = List.toArray s
    let oA = cfg.SpecOut |> List.toArray
    let sIn = MLS.blockStarts s |> List.toArray
    let multO = paths |> Array.map (fun (_, _, bo) -> oA.[bo].Mult)
    let m1s = paths |> Array.map (fun (b1, _, _) -> sA.[b1].Mult)
    let m2s = paths |> Array.map (fun (_, b2, _) -> sA.[b2].Mult)
    let wOff = Array.zeroCreate (paths.Length + 1)
    for p in 0 .. paths.Length - 1 do
        wOff.[p + 1] <- wOff.[p] + multO.[p] * m1s.[p] * m2s.[p]
    let res = Dictionary<int * int, PolyV[]>()
    for p in 0 .. paths.Length - 1 do
        let (b1, b2, bo) = paths.[p]
        let l1, l2, lo = sA.[b1].L, sA.[b2].L, oA.[bo].L
        let cg = WT.realCGDense l1 l2 lo
        let d1, d2 = 2 * l1 + 1, 2 * l2 + 1
        for mo in 0 .. multO.[p] - 1 do
            for u1 in 0 .. m1s.[p] - 1 do
                for u2 in 0 .. m2s.[p] - 1 do
                    let w = d.[wOff.[p] + (mo * m1s.[p] + u1) * m2s.[p] + u2]
                    if w <> 0.0 then
                        let offA = sIn.[b1] + u1 * d1
                        let offB = sIn.[b2] + u2 * d2
                        let arr =
                            match res.TryGetValue ((bo, mo)) with
                            | true, a -> a
                            | _ ->
                                let a = Array.create (2 * lo + 1) (Map.empty : PolyV)
                                res.[(bo, mo)] <- a
                                a
                        for c1 in 0 .. d1 - 1 do
                            for c2 in 0 .. d2 - 1 do
                                for c3 in 0 .. 2 * lo do
                                    let v = w * cg.[c1].[c2].[c3]
                                    if v <> 0.0 then
                                        let cell = List.sort [ offA + c1; offB + c2 ]
                                        arr.[c3] <-
                                            arr.[c3]
                                            |> Map.change cell (fun cur -> Some (defaultArg cur 0.0 + v))
    res

// ---------------------------------------------------------------------------

let runPolyOracleTests () : BlockResult =
    printHeader "Poly Oracle (Sym^k label basis vs isotypic projectors)"
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

    let mkSpec (xs: (int * int * int) list) : MLS.Spec =
        xs |> List.map (fun (l, p, m) -> ({ L = l; Parity = p; Mult = m } : MLS.SpecEntry))

    let anchorA = mkSpec [ (0, 0, 1); (1, 1, 1) ]
    let anchorB = mkSpec [ (1, 1, 2) ]
    let anchorC = mkSpec [ (0, 0, 2); (1, 1, 1) ]
    let anchorD = mkSpec [ (2, 0, 1) ]

    // ========================================================================
    // 1. PROJECTOR EQUALITY
    // ========================================================================
    let anchors =
        [ "A [(0,e,1);(1,o,1)]", anchorA, 2
          "A [(0,e,1);(1,o,1)]", anchorA, 3
          "A [(0,e,1);(1,o,1)]", anchorA, 4
          "B [(1,o,2)]",         anchorB, 2
          "B [(1,o,2)]",         anchorB, 3
          "D [(2,e,1)]",         anchorD, 3 ]

    for (nm, s, k) in anchors do
        let tag = sprintf "%s k=%d" nm k
        let cis = copyInfos s
        let rf = buildRef s k
        let cells = rf.Cells
        let cidx = rf.CellIdx
        let nC = cells.Length

        // ---- side 1: every label's full 2L+1-dim copy, in the frame -------
        let labels = MLS.polyLabels s k
        let vecs =
            labels
            |> List.map (fun lb -> (lb, labelPoly cis lb |> Array.map (toFrame k cells cidx)))

        // orthonormality of the emitted basis (the design's "globally
        // ORTHONORMAL by construction" claim, in the same frame)
        let flat = [| for (_, vs) in vecs do yield! vs |]
        let mutable gramDev = 0.0
        for a in 0 .. flat.Length - 1 do
            for b in a .. flat.Length - 1 do
                let mutable acc = 0.0
                for i in 0 .. nC - 1 do acc <- acc + flat.[a].[i] * flat.[b].[i]
                gramDev <- max gramDev (abs (acc - (if a = b then 1.0 else 0.0)))
        check (sprintf "%s: label basis orthonormal, %d labels / %d vectors (1e-12)" tag labels.Length flat.Length)
              (flat.Length = nC && gramDev < 1e-12)
              (sprintf "dim %d, max Gram dev %.3g" nC gramDev)

        // P_conv per (L, P) = Σ_labels Σ_M v vᵀ
        let conv = Dictionary<int * int, float[][]>()
        for (lb, vs) in vecs do
            let key = (lb.L, lb.Parity)
            let p =
                match conv.TryGetValue key with
                | true, a -> a
                | _ ->
                    let a = Array.init nC (fun _ -> Array.zeroCreate<float> nC)
                    conv.[key] <- a
                    a
            for v in vs do
                for i in 0 .. nC - 1 do
                    if v.[i] <> 0.0 then
                        for j in 0 .. nC - 1 do
                            if v.[j] <> 0.0 then p.[i].[j] <- p.[i].[j] + v.[i] * v.[j]

        // completeness of the convention side alone: Σ_(L,P) P_conv = I
        let mutable compDev = 0.0
        for i in 0 .. nC - 1 do
            for j in 0 .. nC - 1 do
                let mutable acc = 0.0
                for KeyValue (_, p) in conv do acc <- acc + p.[i].[j]
                compDev <- max compDev (abs (acc - (if i = j then 1.0 else 0.0)))
        check (sprintf "%s: Σ_(L,P) P_conv = I (1e-12)" tag) (compDev < 1e-12)
              (sprintf "max dev %.3g" compDev)

        // ---- the exact side's own checks, BEFORE any float ---------------
        check (sprintf "%s: exact tr(P_ref) = (2L+1)·mult, all (L,P)" tag) rf.TraceOk rf.TraceDetail
        check (sprintf "%s: P_ref ≡ 0 at every (L,P) absent from sym_spec (exact)" tag) rf.ZeroOk rf.ZeroDetail
        check (sprintf "%s: frame transform G unitary, P_ref real (1e-12)" tag)
              (rf.UnitaryDev < 1e-12 && rf.MaxImag < 1e-12)
              (sprintf "‖GG†−I‖ %.3g, max |Im P_ref| %.3g" rf.UnitaryDev rf.MaxImag)

        // ---- THE PIN: ‖P_ref − P_conv‖_max --------------------------------
        let keysRef = rf.Proj.Keys |> Seq.toList |> List.sort
        let keysConv = conv.Keys |> Seq.toList |> List.sort
        let mutable worst = 0.0
        let mutable worstAt = ""
        if keysRef = keysConv then
            for key in keysRef do
                let a = rf.Proj.[key]
                let b = conv.[key]
                let mutable w = 0.0
                for i in 0 .. nC - 1 do
                    for j in 0 .. nC - 1 do
                        w <- max w (abs (a.[i].[j] - b.[i].[j]))
                if w > worst then
                    worst <- w
                    worstAt <- sprintf " (worst at L=%d P=%d)" (fst key) (snd key)
        check (sprintf "%s: ‖P_ref − P_conv‖_max < 1e-10 over %d (L,P) sectors" tag keysRef.Length)
              (keysRef = keysConv && worst < 1e-10)
              (if keysRef <> keysConv then sprintf "sector sets differ: %A vs %A" keysRef keysConv
               else sprintf "max %.3g%s" worst worstAt)

    // ========================================================================
    // 2. THE k = 2 M-PIN (§6.9(v)) — alignment table, then the value level
    // ========================================================================
    //
    // PREDICTED RATIOS (derived, not fitted). With w = e_(packed slot at mo=0)
    // embedded into the dense tensor:
    //  - MIRROR cell (b1 < b2): the dropped (b2,b1,bo) slot carries τ = σ and
    //    its CG table is the kept one transposed times σ, so at x = y the two
    //    dense terms coincide and the cell's vector is 2·Σ CG·x·x. The aligned
    //    label is a two-distinct-copy sector, degree 1 per copy (T_{1,l} = I),
    //    one CG coupling, sector constant √(2!/1!1!) = √2 → √2·Σ CG·x·x.
    //    ratio = √2.
    //  - DIAGONAL cell, u1 < u2: same conclusion by the SAME-l exchange
    //    identity (τ·σ = σ² = 1), for BOTH τ signs. ratio = √2.
    //  - DIAGONAL cell, u1 = u2 (only reachable at τ = σ = +1, i.e. L even):
    //    a single dense term Σ CG·x·x, whose frame norm² is
    //    Σ_{c1c2} CG[c1][c2][c3]² = 1 (CG unitarity). The aligned label is a
    //    repeated-copy sector, sector constant √(2!/2!) = 1, and T_{2,l}'s rows
    //    are unit. So |ratio| = 1 — the SIGN is whatever the RREF/GS/lowering
    //    pipeline picked relative to the CG realization, and is reported.
    let k2Specs =
        [ "A [(0,e,1);(1,o,1)]", anchorA
          "B [(1,o,2)]",         anchorB
          "C [(0,e,2);(1,o,1)]", anchorC ]

    for (nm, s) in k2Specs do
        let n = MLS.totalDim s
        let cis = copyInfos s
        let cells = MLS.symMultisets n 2 |> List.toArray
        let cidx = cells |> Array.mapi (fun i c -> (c, i)) |> Map.ofArray
        let nC = cells.Length
        let cfg = MLS.selfTpConfig s
        let paths = MLS.tpPaths cfg |> List.toArray
        let oA = cfg.SpecOut |> List.toArray
        let copyOfOff = cis |> Array.map (fun c -> (c.Off, c.Copy)) |> Map.ofArray
        let denseDim = MLS.tpWeightDim cfg
        let embed = MLS.symTpEmbedTable s

        // ---- stage 1 side: one Sym²(V) vector family per kept cell --------
        let s2cells = MLS.s2TpCells MLS.S2Sym s |> List.toArray
        let mutable buildOk = true
        let mutable buildDetail = ""
        let cellVecs =
            s2cells |> Array.map (fun c ->
                let d = Array.zeroCreate denseDim
                for (ds, ps, sg) in embed do
                    if ps = c.WBase then d.[ds] <- sg
                let q = denseQuadratic s d
                let (_, _, bo) = paths.[c.Path]
                if q.Count <> 1 || not (q.ContainsKey ((bo, 0))) then
                    buildOk <- false
                    buildDetail <- sprintf "packed slot %d hit %d (block, mo) targets" c.WBase q.Count
                let arr = if q.ContainsKey ((bo, 0)) then q.[(bo, 0)] else [| |]
                (c, oA.[bo].L, arr |> Array.map (toFrame 2 cells cidx)))
        check (sprintf "M-pin %s: each packed slot (mo=0) writes exactly one output copy" nm)
              buildOk buildDetail

        // ---- label side ---------------------------------------------------
        let labels = MLS.polyLabels s 2
        let labelVecs =
            labels |> List.map (fun lb -> (lb, labelPoly cis lb |> Array.map (toFrame 2 cells cidx)))
            |> List.toArray

        // ---- the ALIGNMENT TABLE, emitted from the three-row rule ---------
        // cell  ↦ (sorted copy pair of its two multiplicity slots, output L)
        // label ↦ (its sector multiset, its L)
        let cellKey (c: MLS.S2TpCell) =
            let (_, _, bo) = paths.[c.Path]
            (List.sort [ copyOfOff.[c.OffA]; copyOfOff.[c.OffB] ], oA.[bo].L)
        let labKey (lb: MLS.PolyLabel) = (lb.Sector, lb.L)
        let cellKeys = s2cells |> Array.map cellKey
        let labKeys = labelVecs |> Array.map (fst >> labKey)
        let alignOk =
            cellKeys.Length = labKeys.Length
            && (cellKeys |> Array.distinct).Length = cellKeys.Length
            && (labKeys |> Array.distinct).Length = labKeys.Length
            && (List.sort (List.ofArray cellKeys)) = (List.sort (List.ofArray labKeys))
        let labPos = labKeys |> Array.mapi (fun i k -> (k, i)) |> Map.ofArray
        check (sprintf "M-pin %s: label ↔ kept-cell alignment table is a bijection (%d pairs)" nm s2cells.Length)
              alignOk
              (if alignOk then
                 sprintf "%d distinct-copy + %d repeated-copy sectors"
                     (labelVecs |> Array.filter (fun (lb, _) -> lb.Uses.Length = 2) |> Array.length)
                     (labelVecs |> Array.filter (fun (lb, _) -> lb.Uses.Length = 1) |> Array.length)
               else sprintf "cells %A vs labels %A" (List.sort (List.ofArray cellKeys)) (List.sort (List.ofArray labKeys)))

        if alignOk then
            // ---- M = the full change of basis over Sym²(V) ---------------
            // Rows = (kept cell, output component); columns = (label, M).
            // Both index a basis of Sym²(V), so M is square.
            let rows = [| for (c, l, vs) in cellVecs do for ci in 0 .. 2 * l do yield (c, ci, vs.[ci]) |]
            let cols = [| for (lb, vs) in labelVecs do for ci in 0 .. 2 * lb.L do yield (lb, ci, vs.[ci]) |]
            let sq = rows.Length = nC && cols.Length = nC
            let dot (a: float[]) (b: float[]) =
                let mutable acc = 0.0
                for i in 0 .. nC - 1 do acc <- acc + a.[i] * b.[i]
                acc
            // per aligned pair: the ratio (averaged over the 2L+1 components)
            let ratios =
                cellVecs |> Array.map (fun (c, l, vs) ->
                    let j = labPos.[cellKey c]
                    let (_, lvs) = labelVecs.[j]
                    let mutable acc = 0.0
                    for ci in 0 .. 2 * l do acc <- acc + dot vs.[ci] lvs.[ci]
                    acc / float (2 * l + 1))
            let ratioOf = Dictionary<int, float>()
            s2cells |> Array.iteri (fun i c -> ratioOf.[c.WBase] <- ratios.[i])
            let mutable worstAligned = 0.0
            let mutable worstCross = 0.0
            for r in 0 .. rows.Length - 1 do
                let (rc, rci, rv) = rows.[r]
                let rj = labPos.[cellKey rc]
                for cix in 0 .. cols.Length - 1 do
                    let (lb, lci, lv) = cols.[cix]
                    let v = dot rv lv
                    let aligned = (labPos.[labKey lb] = rj) && (lci = rci)
                    if aligned then worstAligned <- max worstAligned (abs (v - ratioOf.[rc.WBase]))
                    else worstCross <- max worstCross (abs v)
            check (sprintf "M-pin %s: M is a label-aligned scaled permutation (%dx%d, 1e-12)" nm nC nC)
                  (sq && worstAligned < 1e-12 && worstCross < 1e-12)
                  (sprintf "aligned blocks ratio·I to %.3g, cross-pairs to %.3g" worstAligned worstCross)

            // ---- the ratios against the DERIVED closed form ---------------
            let mutable ratioOk = true
            let mutable bad = ""
            for i in 0 .. s2cells.Length - 1 do
                let c = s2cells.[i]
                let (lb, _) = labelVecs.[labPos.[cellKey c]]
                let want = if lb.Uses.Length = 2 then sqrt 2.0 else 1.0
                if abs (abs ratios.[i] - want) > 1e-12 then
                    ratioOk <- false
                    bad <- sprintf "cell WBase=%d: |ratio| %.17g, predicted %.17g" c.WBase (abs ratios.[i]) want
            let multiset =
                ratios
                |> Array.map (fun r -> sprintf "%+.4f" r)
                |> Array.countBy id
                |> Array.sortBy fst
                |> Array.map (fun (v, c) -> sprintf "%s x%d" v c)
                |> String.concat ", "
            check (sprintf "M-pin %s: |ratio| = √2 (distinct-copy) / 1 (repeated-copy), derived (1e-12)" nm)
                  ratioOk (if ratioOk then sprintf "observed multiset: %s" multiset else bad)

    printFooter "Poly Oracle" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "Poly Oracle"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
