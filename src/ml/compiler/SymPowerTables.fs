/// T_{j,l} tables — the universal occurrence bases of V_L inside Sym^j(V_l)
/// behind `derive_poly<k>` (retired transforms-as-types plan §3.3b, stage 2b-i).
/// Per (j, l), an exact pipeline with floats appearing only at the very end
/// (the same status as realCGDense): degree-j monomials over the
/// divided-power weight basis (lex `symMultisets` order) → integer raising
/// matrix E → per-weight kernels over ℚ, RREF in lex monomial order (pivot
/// monomials are the labeling artifact) → exact rational Gram–Schmidt within
/// each multiplicity space → integer lowering → diagonal unitarization →
/// realization through WignerTables.uMatrix with the phase rule derived
/// below. Cached per (j, l), like realCGDense.
///
/// CONVENTIONS (stated once; the code follows them):
/// - Weight basis w_m, m = −l..l, 0-based index i = m + l. Divided-power sl₂
///   action: E·w_m = (l−m)·w_{m+1}, F·w_m = (l+m)·w_{m−1}, extended to
///   degree-j monomials as derivations — E on the cell A = ∏ w_i^{α_i} is
///   Σ_i α_i·(2l−i)·(A with one w_i → w_{i+1}), an INTEGER matrix. w_m is
///   the Condon–Shortley unit basis rescaled by c_m = √((l+m)!(l−m)!/(2l)!)
///   (positive, c_m = c_{−m}), so E and F are exactly the CS ladder
///   operators J± written in w coordinates, and lowering-then-normalizing
///   yields the CS-phased unit basis of each occurrence copy.
/// - Inner product: the one induced on Sym^j ⊂ V^{⊗j} by orthonormal {e_i};
///   for arrangement sums, ⟨s_A, s_B⟩ = δ_AB·N_A with N_A = j!/∏α_i!. The
///   equivariant identification of the commutative monomial with a tensor is
///   mono_A ↦ (∏α_i!)·s_A — the unique scaling (up to one global constant)
///   under which the derivation action of E equals Σ_a 1⊗..E..⊗1 on tensors
///   (matching coefficients forces α_m·c_{A'} = (α_{m+1}+1)·c_A, i.e.
///   c_A = ∏α_i!). Hence the exact Gram in stored coordinates is diagonal:
///     ⟨mono_A, mono_B⟩ = δ_AB · j! · ∏_i α_i! · ∏_a c²_{m_a}   — rational.
///   This is where §3.3b identity (3)'s /N_I bookkeeping lives: the emitted
///   float rows satisfy Σ_I T[r,I]·T[r',I]/N_I = δ with no further factors.
///
/// THE REALIZATION PHASE RULE — §3.3b's conjecture, derived here against the
/// conventions AS CODED (WignerTables.uMatrix, the CS basis of `clebsch`),
/// and CONFIRMED: the complex table of a V_L occurrence in Sym^j(V_l) is
/// purely real iff j·l + L is even and purely imaginary iff odd; the
/// canonical realization multiplies by −i exactly in the odd case.
///
///  1. The coded uMatrix rows all satisfy u[c, m] = (−1)^m·conj(u[c, −m])
///     (check the three row shapes at WignerTables.fs): they are the fixed
///     vectors of the antiunitary J with J|l,m⟩ = (−1)^m|l,−m⟩. In the CS
///     convention conj(D^l_{mm'}) = (−1)^{m−m'}·D^l_{−m,−m'}, so J commutes
///     with the whole real rotation action; and J w_m = (−1)^m w_{−m}
///     because the c_m rescaling is positive and mirror-symmetric.
///  2. Let K be plain coefficient conjugation in the w basis — the map every
///     vector this file builds is trivially fixed under (kernel, RREF,
///     Gram–Schmidt and lowering are rational arithmetic; the final
///     normalization is positive). Since R_y(π)|l,m⟩ = (−1)^{l−m}|l,−m⟩,
///     K = (−1)^l·J∘R_y(π) on V_l; inducing to Sym^j multiplies the scalar
///     once per factor: K_S = (−1)^{j·l}·J_S∘R_S, where R_S is the honest
///     GROUP action of R_y(π) on Sym^j(V_l) and J_S the induced conjugation.
///  3. R_S preserves each occurrence copy — it is a group element and the
///     copy (the sl₂ span of a highest-weight vector) is invariant — acting
///     by the standard d^L(π): v_{r,M} ↦ (−1)^{L−M}·v_{r,−M}. So the a
///     priori worry that conjugation mixes the copies of a multiplicity
///     space dissolves: J_S v_{r,M} = (−1)^{jl}·K_S(R_S⁻¹ v_{r,M})
///     = (−1)^{jl+L−M}·v_{r,−M}, copy by copy. Reading J_S in w-monomial
///     coordinates (real coefficients, weight M: J_S negates weights and
///     contributes (−1)^{Σm_a} = (−1)^M), the complex table t[M, A] has the
///     mirror symmetry  t[−M, mirror(A)] = (−1)^{jl+L}·t[M, A].
///  4. Realize exactly like realCGDense — conj(u_l) on every Sym-side
///     factor, u_L plain on the row side. Taking conj of the realized table:
///     each U entry flips by its m-sign (step 1), the weight selection
///     Σ m_a = M cancels the accumulated (−1)^{Σm_a + M}, and the m → −m
///     relabeling leaves  conj(T_real) = (−1)^{jl+L}·T_real.  Real iff
///     j·l + L is even; purely imaginary, realized by −i, iff odd.  ∎
///
/// At j = 2 (Sym² carries even L only, Λ² odd) this reduces to realCGDense's
/// l1+l2+l3 parity rule at l1 = l2 = l; j = 1 gives the always-real identity
/// table; the first genuinely new case is V₃ ⊂ Sym³(V₂) — imaginary,
/// realized by −i. The generator still asserts the residual gap (≥ 5 orders
/// below the table max on the realized branch, the other branch at table
/// scale) — a violation is a compiler bug and fails loudly, not a data
/// condition. Parity never enters: O(3) parity acts on Sym^j(V_{l,p}) by the
/// scalar (−1)^{j·p} and rides at spec level.
module Blade.ML.SymPowerTables

open System.Collections.Generic
open System.Numerics

module Spec = Blade.ML.Spec
module WT = Blade.ML.WignerTables

// ---------------------------------------------------------------------------
// Minimal exact rationals over System.Numerics.BigInteger. Local by design:
// MLSpec stays dependency-free (it is shared with the BladeML project) and
// nothing else compiler-side needs exact fractions. Always normalized
// (Den > 0, gcd = 1), so structural equality is value equality. Exactness
// comes from BigInteger arithmetic — functionally the same result as the
// fraction-free/Bareiss route, chosen for clarity at these tiny sizes.
// ---------------------------------------------------------------------------

type Rat = { Num: bigint; Den: bigint }

[<RequireQualifiedAccess>]
module Rat =
    let make (n: bigint) (d: bigint) : Rat =
        if d.IsZero then failwith "internal: SymPowerTables rational with zero denominator"
        let n, d = if d.Sign < 0 then -n, -d else n, d
        let g = BigInteger.GreatestCommonDivisor(n, d)
        if g.IsOne then { Num = n; Den = d } else { Num = n / g; Den = d / g }
    let zero = { Num = BigInteger.Zero; Den = BigInteger.One }
    let one = { Num = BigInteger.One; Den = BigInteger.One }
    let ofBigInt (n: bigint) = { Num = n; Den = BigInteger.One }
    let ofInt (n: int) = ofBigInt (bigint n)
    let isZero (a: Rat) = a.Num.IsZero
    let add (a: Rat) (b: Rat) = make (a.Num * b.Den + b.Num * a.Den) (a.Den * b.Den)
    let sub (a: Rat) (b: Rat) = make (a.Num * b.Den - b.Num * a.Den) (a.Den * b.Den)
    let mul (a: Rat) (b: Rat) = make (a.Num * b.Num) (a.Den * b.Den)
    let div (a: Rat) (b: Rat) =
        if b.Num.IsZero then failwith "internal: SymPowerTables rational division by zero"
        make (a.Num * b.Den) (a.Den * b.Num)
    let neg (a: Rat) = { a with Num = -a.Num }
    let scaleInt (k: int) (a: Rat) = make (a.Num * bigint k) a.Den
    let toFloat (a: Rat) = float a.Num / float a.Den

// ---------------------------------------------------------------------------
// The monomial cell space of Sym^j(V_l): lex `symMultisets` cells over the
// 2l+1 weight indices, with the integer E/F actions and the exact Gram
// weights precomputed once per (j, l).
// ---------------------------------------------------------------------------

let private fact (n: int) : bigint =
    let mutable acc = BigInteger.One
    for i in 2 .. n do acc <- acc * bigint i
    acc

let rec private removeFirst (x: int) (xs: int list) =
    match xs with
    | [] -> []
    | h :: t -> if h = x then t else h :: removeFirst x t

/// Distinct arrangements (ordered tuples) of a sorted multiset. j ≤ 4, so
/// brute recursion over distinct heads is plenty.
let rec private arrangements (xs: int list) : int list list =
    match xs with
    | [] -> [ [] ]
    | _ ->
        xs
        |> List.distinct
        |> List.collect (fun x -> arrangements (removeFirst x xs) |> List.map (fun r -> x :: r))

type private CellSpace = {
    J: int
    Ell: int
    /// lex `symMultisets` cells (0-based weight indices i = m + l)
    Cells: int list []
    Index: Map<int list, int>
    /// Σ i_a − j·l
    Weight: int []
    /// ∏_i α_i! per cell, as float (≤ 24 at j ≤ 4)
    AlphaFact: float []
    /// N_A = j!/∏α_i! per cell
    NMult: int []
    /// exact Gram weight q_A = j!·∏α_i!·∏_a c²_{m_a} per cell (module doc)
    Q: Rat []
    /// ∏_a c_{m_a} per cell (float; c_m = √((l+m)!(l−m)!/(2l)!))
    CProd: float []
    /// integer E action per cell: (target cell index, coefficient)
    EAct: (int * int) [] []
    /// integer F action per cell: (target cell index, coefficient)
    FAct: (int * int) [] []
    /// distinct ordered arrangements per cell (for the realization step)
    Arrs: int list [] []
}

let private spaceCache = Dictionary<int * int, CellSpace>()

let private cellSpace (j: int) (l: int) : CellSpace =
    if j < 1 || j > 4 then
        failwithf "internal: SymPowerTables j = %d out of scope (1..4, plan §6.5)" j
    if l < 0 then failwithf "internal: SymPowerTables negative l (%d)" l
    match spaceCache.TryGetValue((j, l)) with
    | true, v -> v
    | _ ->
        let d = 2 * l + 1
        let cells = Spec.symMultisets d j |> List.toArray
        let index = cells |> Array.mapi (fun i c -> (c, i)) |> Map.ofArray
        let alphas (cell: int list) = cell |> List.countBy id
        // c²_i = i!·(2l−i)!/(2l)! as an exact rational, per weight index i
        let c2 = Array.init d (fun i -> Rat.make (fact i * fact (2 * l - i)) (fact (2 * l)))
        let space = {
            J = j
            Ell = l
            Cells = cells
            Index = index
            Weight = cells |> Array.map (fun c -> List.sum c - j * l)
            AlphaFact =
                cells |> Array.map (fun c ->
                    alphas c |> List.fold (fun acc (_, a) -> acc * float (fact a)) 1.0)
            NMult =
                cells |> Array.map (fun c ->
                    int (alphas c |> List.fold (fun acc (_, a) -> acc / fact a) (fact j)))
            Q =
                cells |> Array.map (fun c ->
                    let af = alphas c |> List.fold (fun acc (_, a) -> acc * fact a) BigInteger.One
                    c |> List.fold (fun acc i -> Rat.mul acc c2.[i]) (Rat.ofBigInt (fact j * af)))
            CProd =
                cells |> Array.map (fun c ->
                    c |> List.fold (fun acc i -> acc * sqrt (Rat.toFloat c2.[i])) 1.0)
            EAct =
                cells |> Array.map (fun cell ->
                    alphas cell
                    |> List.choose (fun (i, a) ->
                        if i < 2 * l then
                            let target = List.sort (i + 1 :: removeFirst i cell)
                            Some (index.[target], a * (2 * l - i))
                        else None)
                    |> List.toArray)
            FAct =
                cells |> Array.map (fun cell ->
                    alphas cell
                    |> List.choose (fun (i, a) ->
                        if i > 0 then
                            let target = List.sort (i - 1 :: removeFirst i cell)
                            Some (index.[target], a * i)
                        else None)
                    |> List.toArray)
            Arrs = cells |> Array.map (fun c -> arrangements c |> List.toArray)
        }
        spaceCache.[(j, l)] <- space
        space

let private applyAct (act: (int * int) [] []) (v: Rat[]) : Rat[] =
    let out = Array.create v.Length Rat.zero
    for i in 0 .. v.Length - 1 do
        if not (Rat.isZero v.[i]) then
            for (tgt, c) in act.[i] do
                out.[tgt] <- Rat.add out.[tgt] (Rat.scaleInt c v.[i])
    out

let private gramDotSp (sp: CellSpace) (a: Rat[]) (b: Rat[]) : Rat =
    let mutable acc = Rat.zero
    for i in 0 .. a.Length - 1 do
        if not (Rat.isZero a.[i]) && not (Rat.isZero b.[i]) then
            acc <- Rat.add acc (Rat.mul sp.Q.[i] (Rat.mul a.[i] b.[i]))
    acc

// ---- public exact re-verification hooks (the tests use these, so the pins
// re-derive E·v = 0 and the Gram identities rather than trusting the builder) --

/// The lex monomial cells of Sym^j over the 2l+1 weight indices i = m + l.
let symCells (j: int) (l: int) : int list list = cellSpace j l |> fun sp -> List.ofArray sp.Cells

/// Integer raising action E (divided-power convention, module doc) on a
/// coefficient vector over the full cell space.
let applyE (j: int) (l: int) (v: Rat[]) : Rat[] = applyAct (cellSpace j l).EAct v

/// Integer lowering action F on a coefficient vector over the full cell space.
let applyF (j: int) (l: int) (v: Rat[]) : Rat[] = applyAct (cellSpace j l).FAct v

/// The exact Sym^j inner product in stored (monomial-cell) coordinates:
/// Σ_A q_A·a_A·b_A with q_A = j!·∏α_i!·∏ c²_{m_a} (module doc).
let gramDot (j: int) (l: int) (a: Rat[]) (b: Rat[]) : Rat = gramDotSp (cellSpace j l) a b

// ---------------------------------------------------------------------------
// Exact layer: per weight L, the kernel of E over ℚ in canonical RREF form,
// Gram–Schmidt'd in RREF row order.
// ---------------------------------------------------------------------------

/// One weight space's worth of occurrences of V_L in Sym^j(V_l), exact.
type ExactWeightSpace = {
    L: int
    /// cell indices of the weight-L monomials (ascending = lex order)
    WeightCells: int []
    /// pivot monomial (cell) of each RREF kernel row, in RREF row order —
    /// the documented labeling artifact of §3.3b
    Pivots: int list []
    /// canonical RREF kernel rows over the FULL cell space (support =
    /// weight-L cells); copy r of the occurrence = row r
    RrefRows: Rat[][]
    /// exact Gram–Schmidt of RrefRows in row order (unnormalized)
    GsRows: Rat[][]
    /// ⟨GsRows r, GsRows r⟩ — the rational norm² factored out at float time
    Norm2: Rat []
}

/// Reduced row echelon form over ℚ. Returns (row, pivot column) pairs with
/// pivots ascending; zero rows dropped. Canonical for the row space given
/// the (lex) column order — this IS the §3.3b occurrence labeling.
let private rref (rows: Rat[][]) : (Rat[] * int) list =
    let rows = rows |> Array.map Array.copy
    let nRows = rows.Length
    let nCols = if nRows = 0 then 0 else rows.[0].Length
    let mutable pivRow = 0
    let pivots = ResizeArray<int>()
    for col in 0 .. nCols - 1 do
        if pivRow < nRows then
            let mutable sel = -1
            for r in pivRow .. nRows - 1 do
                if sel < 0 && not (Rat.isZero rows.[r].[col]) then sel <- r
            if sel >= 0 then
                let tmp = rows.[pivRow]
                rows.[pivRow] <- rows.[sel]
                rows.[sel] <- tmp
                let piv = rows.[pivRow].[col]
                for c in 0 .. nCols - 1 do
                    rows.[pivRow].[c] <- Rat.div rows.[pivRow].[c] piv
                for r in 0 .. nRows - 1 do
                    if r <> pivRow && not (Rat.isZero rows.[r].[col]) then
                        let f = rows.[r].[col]
                        for c in 0 .. nCols - 1 do
                            rows.[r].[c] <- Rat.sub rows.[r].[c] (Rat.mul f rows.[pivRow].[c])
                pivots.Add col
                pivRow <- pivRow + 1
    [ for r in 0 .. pivots.Count - 1 -> (rows.[r], pivots.[r]) ]

/// Kernel basis of a constraint matrix (rows = constraints), one vector per
/// free column, via the RREF pivot/free split.
let private kernelBasis (m: Rat[][]) (nCols: int) : Rat[][] =
    let red = rref m
    let pivotCols = red |> List.map snd |> Set.ofList
    [| for f in 0 .. nCols - 1 do
         if not (pivotCols.Contains f) then
             let v = Array.create nCols Rat.zero
             v.[f] <- Rat.one
             for (row, p) in red do
                 v.[p] <- Rat.neg row.[f]
             yield v |]

let private exactCache = Dictionary<int * int, ExactWeightSpace list>()

/// The exact occurrence data of Sym^j(V_l), L descending, mult > 0 only.
/// Cross-checked against the §3.3 weight-peel (`powerSpec`) on every build.
let symPowerExact (j: int) (l: int) : ExactWeightSpace list =
    match exactCache.TryGetValue((j, l)) with
    | true, v -> v
    | _ ->
        let sp = cellSpace j l
        let nCells = sp.Cells.Length
        // Occurrence multiplicities from the integer weight-peel — the
        // stage-2a counting half; the kernel dims must reproduce it.
        let expected =
            Spec.powerSpec Spec.PowSym [ ({ L = l; Parity = 0; Mult = 1 } : Spec.SpecEntry) ] j
            |> List.map (fun e -> e.L, e.Mult)
            |> Map.ofList
        let spaces = ResizeArray<ExactWeightSpace>()
        for L in j * l .. -1 .. 0 do
            let cols = [| for i in 0 .. nCells - 1 do if sp.Weight.[i] = L then yield i |]
            let rowsIdx = [| for i in 0 .. nCells - 1 do if sp.Weight.[i] = L + 1 then yield i |]
            let rowPos = rowsIdx |> Array.mapi (fun p i -> (i, p)) |> Map.ofArray
            let mat = Array.init rowsIdx.Length (fun _ -> Array.create cols.Length Rat.zero)
            cols |> Array.iteri (fun cPos cellIdx ->
                for (tgt, coeff) in sp.EAct.[cellIdx] do
                    mat.[rowPos.[tgt]].[cPos] <- Rat.add mat.[rowPos.[tgt]].[cPos] (Rat.ofInt coeff))
            let canon = rref (kernelBasis mat cols.Length)
            let mult = canon.Length
            let expectedMult = expected |> Map.tryFind L |> Option.defaultValue 0
            if mult <> expectedMult then
                failwithf "internal: SymPowerTables(%d,%d): ker E at weight %d has dim %d but powerSpec says %d"
                    j l L mult expectedMult
            if mult > 0 then
                let toFull (v: Rat[]) =
                    let full = Array.create nCells Rat.zero
                    cols |> Array.iteri (fun cPos cellIdx -> full.[cellIdx] <- v.[cPos])
                    full
                let rrefFull = canon |> List.map (fst >> toFull) |> List.toArray
                let pivots = canon |> List.map (fun (_, p) -> sp.Cells.[cols.[p]]) |> List.toArray
                // Exact Gram–Schmidt in RREF row order (dims ≤ 3 at j ≤ 4;
                // independence is already proved by the pivots).
                let gs = ResizeArray<Rat[]>()
                let norm2 = ResizeArray<Rat>()
                for v0 in rrefFull do
                    let v = Array.copy v0
                    for s in 0 .. gs.Count - 1 do
                        let coef = Rat.div (gramDotSp sp v gs.[s]) norm2.[s]
                        if not (Rat.isZero coef) then
                            let prev = gs.[s]
                            for i in 0 .. nCells - 1 do
                                v.[i] <- Rat.sub v.[i] (Rat.mul coef prev.[i])
                    let n2 = gramDotSp sp v v
                    if Rat.isZero n2 then
                        failwithf "internal: SymPowerTables(%d,%d) L=%d: Gram–Schmidt hit a zero norm" j l L
                    gs.Add v
                    norm2.Add n2
                // Re-verify E·v = 0 exactly for every RREF and GS row.
                for v in Seq.append (Seq.ofArray rrefFull) gs do
                    if applyAct sp.EAct v |> Array.exists (fun x -> not (Rat.isZero x)) then
                        failwithf "internal: SymPowerTables(%d,%d) L=%d: kernel vector not annihilated by E" j l L
                spaces.Add { L = L; WeightCells = cols; Pivots = pivots
                             RrefRows = rrefFull; GsRows = gs.ToArray(); Norm2 = norm2.ToArray() }
        let total = spaces |> Seq.sumBy (fun w -> w.RrefRows.Length * (2 * w.L + 1))
        if total <> nCells then
            failwithf "internal: SymPowerTables(%d,%d): occurrences cover %d of %d dimensions" j l total nCells
        let res = List.ofSeq spaces
        exactCache.[(j, l)] <- res
        res

// ---------------------------------------------------------------------------
// Float layer: integer lowering, diagonal unitarization, uMatrix realization
// with the derived phase rule.
// ---------------------------------------------------------------------------

/// One realized occurrence copy of V_L in Sym^j(V_l).
type SymOccurrence = {
    L: int
    /// copy index within the L multiplicity space (RREF row order)
    Copy: int
    /// this copy's RREF pivot monomial (the labeling artifact)
    Pivot: int list
    /// true iff the realization multiplied by −i (the derived rule:
    /// j·l + L odd); asserted against the empirical residuals at build time
    Flipped: bool
    /// max |Re| / max |Im| over the complex block BEFORE the phase fix
    MaxRe: float
    MaxIm: float
    /// Rows.[c].[I], c = m_real + L: T[row, I] = ⟨v_row, s_I⟩ over the lex
    /// real-basis cells — §3.3b identity (2); evaluation carries no N_I,
    /// orthonormality is Σ_I T[r,I]·T[r',I]/N_I = δ (identity (3)).
    Rows: float[][]
}

/// The realized table family of Sym^j(V_l).
type SymPowerTable = {
    J: int
    Ell: int
    /// lex monomial cells over the 2l+1 REAL components (same enumeration
    /// as the complex side: `symMultisets (2l+1) j`)
    Cells: int list []
    /// N_I per cell, as float — the /N_I weight of identity (3)
    CellMult: float []
    /// occurrences: L descending, copies in RREF order
    Occurrences: SymOccurrence list
}

let private tableCache = Dictionary<int * int, SymPowerTable>()

/// The realized T_{j,l} table, cached per (j, l) like realCGDense.
let symPowerTable (j: int) (l: int) : SymPowerTable =
    match tableCache.TryGetValue((j, l)) with
    | true, v -> v
    | _ ->
        let sp = cellSpace j l
        let nCells = sp.Cells.Length
        let exact = symPowerExact j l
        let ul = WT.uMatrix l
        // v_mu = Σ_c conj(U[c, mu])·e_c — the nonzero (c, conj u) options per
        // weight index, exactly realCGDense's conj-on-the-input-side placement.
        let colOpts =
            [| for i in 0 .. 2 * l ->
                 [| for c in 0 .. 2 * l do
                      let u = ul.[c].[i]
                      if u <> Complex.Zero then yield (c, Complex.Conjugate u) |] |]
        let occs = ResizeArray<SymOccurrence>()
        for ws in exact do
            let L = ws.L
            let uL = WT.uMatrix L
            for r in 0 .. ws.GsRows.Length - 1 do
                // Complex block: rows = real components of V_L, cols = real cells.
                let cplx = Array.init (2 * L + 1) (fun _ -> Array.zeroCreate<Complex> nCells)
                let mutable vec = ws.GsRows.[r]
                for step in 0 .. 2 * L do
                    let M = L - step
                    if step > 0 then vec <- applyAct sp.FAct vec  // integer lowering
                    let n2 = gramDotSp sp vec vec
                    if Rat.isZero n2 then
                        failwithf "internal: SymPowerTables(%d,%d) L=%d copy %d: lowering lost the vector at M=%d" j l L r M
                    let invNorm = 1.0 / sqrt (Rat.toFloat n2)   // diagonal unitarization
                    // Per-M realization: expand each monomial's distinct
                    // arrangements factor-by-factor through conj(U).
                    let dM = Array.zeroCreate<Complex> nCells
                    for iA in 0 .. nCells - 1 do
                        if not (Rat.isZero vec.[iA]) then
                            // tensor coefficient per ordered arrangement:
                            // a_A·∏α_i! (module doc), w→v rescale ∏c_m, unit norm
                            let beta = Rat.toFloat vec.[iA] * sp.AlphaFact.[iA] * sp.CProd.[iA] * invNorm
                            for arr in sp.Arrs.[iA] do
                                let rec go (rest: int list) (acc: Complex) (chosen: int list) =
                                    match rest with
                                    | [] ->
                                        let idx = sp.Index.[List.sort chosen]
                                        dM.[idx] <- dM.[idx] + acc
                                    | i :: tl ->
                                        for (c, w) in colOpts.[i] do
                                            go tl (acc * w) (c :: chosen)
                                go arr (Complex(beta, 0.0)) []
                    // fold into the rows through u_L (plain, output side)
                    for cL in 0 .. 2 * L do
                        let uu = uL.[cL].[M + L]
                        if uu <> Complex.Zero then
                            for idx in 0 .. nCells - 1 do
                                if dM.[idx] <> Complex.Zero then
                                    cplx.[cL].[idx] <- cplx.[cL].[idx] + uu * dM.[idx]
                // Phase: apply the DERIVED rule, then assert the residual gap —
                // the realized branch ≥ 5 orders below max, the other at table
                // scale (unit rows bound max|T| ≥ 1/√nCells from below).
                let mutable maxRe = 0.0
                let mutable maxIm = 0.0
                for row in cplx do
                    for v in row do
                        maxRe <- max maxRe (abs v.Real)
                        maxIm <- max maxIm (abs v.Imaginary)
                let flipped = (j * l + L) % 2 = 1
                let big = max maxRe maxIm
                let resid = if flipped then maxRe else maxIm
                if resid > 1e-10 * big || big < 0.05 then
                    failwithf "internal: SymPowerTables(%d,%d) L=%d copy %d: phase rule violated or residuals not gapped (maxRe %g, maxIm %g, rule says %s)"
                        j l L r maxRe maxIm (if flipped then "imaginary" else "real")
                let phase = if flipped then Complex(0.0, -1.0) else Complex.One
                let rows = cplx |> Array.map (Array.map (fun v -> (v * phase).Real))
                // Unit-row sanity under identity (3)'s /N_I weighting.
                for row in rows do
                    let mutable s = 0.0
                    for i in 0 .. nCells - 1 do
                        s <- s + row.[i] * row.[i] / float sp.NMult.[i]
                    if abs (s - 1.0) > 1e-10 then
                        failwithf "internal: SymPowerTables(%d,%d) L=%d copy %d: realized row norm² = %.17g" j l L r s
                occs.Add { L = L; Copy = r; Pivot = ws.Pivots.[r]
                           Flipped = flipped; MaxRe = maxRe; MaxIm = maxIm; Rows = rows }
        let res = {
            J = j
            Ell = l
            Cells = sp.Cells
            CellMult = sp.NMult |> Array.map float
            Occurrences = List.ofSeq occs
        }
        tableCache.[(j, l)] <- res
        res
