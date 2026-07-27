/// Point groups — the second BLOCK-SPEC member of the transforms-as-types
/// discipline (plan-transforms-as-types §3.6's 5b subsection, §7 stage 5,
/// sub-stage 5b-0). This file is the COUNTING half: frozen integer character
/// tables for {C4, D4}, their load-time integrity certificate, the generic
/// e-weighted Hom counting core, and the `pg*` sizing wrappers. No emission,
/// no tag grammar, no certification lattice — 5b-i adds the index-type former
/// and `ml.derive_pg_linear`, 5b-ii the MLEquiv arm.
///
/// Dependency-free on purpose (not even MLSpec): §3.6's "twin-not-reroute"
/// discipline says the generic core below is a TEST-PINNED TWIN of
/// MLSpec.homDim/homBlocks, not a replacement for it. MLSpec stays
/// byte-untouched; the pin lives in tests/Test_PointSpec.fs, which is the only
/// place the two modules meet. Rerouting O(3) through the generic core is
/// earned at the THIRD block-spec member, not here.
///
/// ---------------------------------------------------------------------------
/// WHY THERE IS A CHARACTER TABLE HERE (and why MLPermSpec.fs has none)
/// ---------------------------------------------------------------------------
/// Sₙ (stage 5a) is an INDEX-ACTION member: its layer algebra is orbit
/// combinatorics on `Idx<N>` powers and never needs an irrep. Point groups are
/// the opposite: the module is a direct sum of labelled irreducible blocks and
/// the Hom space is read off the labels by Schur. So this file carries baked
/// matrices where MLPermSpec.fs carries partitions — the split is the design,
/// not an accident of implementation order.
///
/// ---------------------------------------------------------------------------
/// THE FROBENIUS–SCHUR CORRECTION — the one thing O(3) let us forget
/// ---------------------------------------------------------------------------
/// Over ℝ, Schur's lemma does not say "one free scalar per (input copy, output
/// copy) pair". It says: one free element of the DIVISION ALGEBRA
/// D_i = End_G(U_i), which is ℝ, ℂ or ℍ. Writing e_i = dim_ℝ D_i ∈ {1, 2, 4},
/// the count is the FS formula — stated once, at `genericHomDim` below, and
/// nowhere else in this file.
///
/// Every O(3) irrep is of real type (e = 1), which is exactly why MLSpec.homDim
/// can be `Σ mᵢ·nᵢ` with no correction factor and still be right. The moment a
/// second block-spec family arrives the factor becomes visible: C4's E label is
/// of COMPLEX type (e = 2) while D4's E label — same dimension, same R₉₀
/// generator — is of REAL type (e = 1). The corpus diff between those two is
/// the whole thesis of stage 5b: `pgHomDim` on the same spec SHAPE is 9 at C4
/// and 5 at D4, and the FS correction is the only difference.
///
/// The emitted basis of a cell is [Id] at e = 1 and [Id, J] at e = 2, with J a
/// BAKED per-label matrix (see `endBasis`). J is basis-relative data — there is
/// no call site at which it could be "derived", because it depends on the
/// chosen real form — so it lives in the table beside the generators and is
/// certified there.
///
/// ---------------------------------------------------------------------------
/// THE TABLES ARE DATA, AND THE INTEGRITY CERTIFICATE IS THE PROOF OBLIGATION
/// ---------------------------------------------------------------------------
/// `certifyPointGroup` runs on every registry fetch (memoized per name; |G| ≤ 8
/// so the cost is microseconds either way) and failwithf's on any violation —
/// these are compiler bugs, not user errors, in the `MLPermSpec.certify` house
/// style. Six families, all integer-exact:
///
///   1. SHAPE. Distinct label names; every generator and J square of side
///      DimR; every irrep carries the same generator count as the group's
///      generator NAMES, so a common word set is meaningful.
///   2. CLOSURE. Elements are enumerated by BFS over the generators from the
///      identity, an element being identified by its TUPLE of matrices across
///      ALL labels (that tuple is faithful — the irreps of a finite group
///      separate its elements — while no single label need be). The word each
///      element carries is therefore a COMMON word set: one word, evaluated in
///      every label. The closure count must equal the declared |G|, and the
///      enumerated set must be closed under multiplication.
///   3. ORTHOGONALITY. MᵀM = Id for every element matrix. Basis-relative, and
///      true of these tables by construction (§3.6 picked C4/D4 for MATRIX
///      RATIONALITY: every entry is in {0, ±1}). It is asserted because the
///      5b-0 oracle's Reynolds form `M ↦ ρ_W(g)·M·ρ_V(g)ᵀ` is the correct
///      group average only when ρ(g)ᵀ = ρ(g)⁻¹.
///   4. THE FS INDICATOR. ν(U) = Σ_g χ_U(g²) / |G|, computed from the
///      enumerated elements by squaring each matrix and tracing — integer, and
///      integer-divisible by |G| (also asserted). See `fsIndicator` for the
///      value convention, which is a REAL-character convention and therefore
///      reads 1 / 0 / −2, not the textbook 1 / 0 / −1.
///   5. THE J IDENTITIES. Where a label declares J: J² = −Id and J·g = g·J for
///      every GENERATOR matrix (generators suffice — commutation is closed
///      under products). Declared Fs and declared J must agree: complex type
///      requires a J, real type forbids one. Note what is NOT asserted: that no
///      valid J exists for D4's E. Absence is design DATA (D4's E is of real
///      type, so End is one-dimensional and any J would have to be a scalar
///      with J² = −Id, which ℝ does not provide) — the fsIndicator check is the
///      one that carries that content, and it does so positively.
///   6. THE ℝ-BURNSIDE TRAP. Σᵢ dᵢ²/eᵢ = |G|, i.e. dim_ℝ ℝ[G] read off the
///      Wedderburn decomposition ℝ[G] ≅ ⊕ᵢ M_{nᵢ}(Dᵢ) with dᵢ = nᵢ·eᵢ. This is
///      the trap that catches a MISSING label, a wrong dimension and a wrong FS
///      type in one integer: C4 gives 1 + 1 + 4/2 = 4 and D4 gives
///      1 + 1 + 1 + 1 + 4 = 8. Dropping C4's E, or calling it real type, breaks
///      it immediately.
///
/// ---------------------------------------------------------------------------
/// THE ROSTER BOUNDARY IS RATIONALITY, NOT CRYSTALLOGRAPHY
/// ---------------------------------------------------------------------------
/// {C4, D4} is not a claim about which point groups matter; it is the pair for
/// which every generator entry lies in {0, ±1}, so the oracle is exact-rational
/// with no field extension and runtime equivariance pins are exact float
/// equality. The ℚ(√3) families (trigonal / hexagonal / cubic-E) are the named
/// first growth (§3.6), and they need a coefficient ring before they need a
/// table.
///
/// FsQuat is likewise a RESERVED VALUE, not a dead field: FS ∈ {ℝ, ℂ} for all
/// single point groups and ℍ first appears at double groups. Counting is
/// uniform in e — `endDim FsQuat = 4` and the FS formula reads it like any
/// other — while every emission-adjacent path (`endBasis`) raises a loud
/// internal error. The count arm is exercised by a synthetic label in the
/// tests; nothing in the shipped registry reaches the emission arm.
module Blade.ML.PointSpec

open System.Collections.Generic

// ---------------------------------------------------------------------------
// Frobenius–Schur types
// ---------------------------------------------------------------------------

/// The Frobenius–Schur type of an ℝ-irreducible label: which division algebra
/// its endomorphism ring is. `FsQuat` is reserved (see the header) — legal in
/// every counting path, an internal error in every emitting one.
type FsType =
    | FsReal
    | FsComplex
    | FsQuat

/// e = dim_ℝ End_G(U) ∈ {1, 2, 4} — the FS weight of a label, and the ONLY
/// thing the counting core ever asks about an FsType.
let endDim (fs: FsType) : int =
    match fs with
    | FsReal -> 1
    | FsComplex -> 2
    | FsQuat -> 4

/// The one-letter division-algebra name, for diagnostics only.
let fsName (fs: FsType) : string =
    match fs with
    | FsReal -> "R"
    | FsComplex -> "C"
    | FsQuat -> "H"

// ---------------------------------------------------------------------------
// Integer matrices. Rectangular is never needed here — every matrix in this
// file is a square rep matrix — so the helpers assume and check squareness.
// ---------------------------------------------------------------------------

let matId (n: int) : int[][] =
    Array.init n (fun i -> Array.init n (fun j -> if i = j then 1 else 0))

let matIsSquare (n: int) (m: int[][]) : bool =
    m.Length = n && m |> Array.forall (fun r -> r.Length = n)

let matMul (a: int[][]) (b: int[][]) : int[][] =
    let n = a.Length
    if b.Length <> n then
        failwithf "internal: MLPointSpec.matMul on mismatched sizes (%d vs %d)" n b.Length
    Array.init n (fun i ->
        Array.init n (fun j ->
            let mutable acc = 0
            for k in 0 .. n - 1 do acc <- acc + a.[i].[k] * b.[k].[j]
            acc))

let matTranspose (a: int[][]) : int[][] =
    let n = a.Length
    Array.init n (fun i -> Array.init n (fun j -> a.[j].[i]))

let matNeg (a: int[][]) : int[][] =
    a |> Array.map (Array.map (fun v -> -v))

let matEq (a: int[][]) (b: int[][]) : bool =
    a.Length = b.Length && Array.forall2 (fun (r: int[]) (s: int[]) -> r = s) a b

let matTrace (a: int[][]) : int =
    let mutable acc = 0
    for i in 0 .. a.Length - 1 do acc <- acc + a.[i].[i]
    acc

/// A stable textual key for a tuple of matrices — the element identity used by
/// the closure BFS. Deliberately a string rather than structural array
/// equality, because `int[][] list` does not hash structurally in .NET.
let private tupleKey (ms: int[][] list) : string =
    ms
    |> List.map (fun m ->
        m |> Array.map (fun r -> r |> Array.map string |> String.concat ",") |> String.concat ";")
    |> String.concat "|"

// ---------------------------------------------------------------------------
// The table types
// ---------------------------------------------------------------------------

/// One ℝ-irreducible label. `Gens` is parallel to the group's `GenNames`;
/// `J` is the baked complex structure (Some exactly at `FsComplex`).
type PgIrrep = {
    Name: string
    DimR: int
    Fs: FsType
    Gens: int[][] list
    J: int[][] option
}

/// A point group as frozen table data. `GenNames` is diagnostic-only (it lets
/// the integrity messages and the closure words name a generator instead of an
/// index) and fixes the generator ORDER that every label's `Gens` follows.
type PointGroup = {
    Name: string
    Irreps: PgIrrep list
    Order: int
    GenNames: string list
}

/// One enumerated group element: the word that produced it (indices into
/// `GenNames`, left-to-right) and its matrix in EVERY label, parallel to
/// `Irreps`. One word, all labels — the common word set of the header.
type PgElement = {
    Word: int list
    Mats: int[][] list
}

// ---------------------------------------------------------------------------
// THE CANONICAL TABLES — frozen data, fixed by the 5b design round. Every
// entry is in {0, ±1} (the rationality boundary of the header).
// ---------------------------------------------------------------------------

/// R₉₀ = [[0, −1], [1, 0]] — the 90° rotation, the E-block generator of both
/// shipped groups, and (at C4) also the baked J.
let private r90 : int[][] = [| [| 0; -1 |]; [| 1; 0 |] |]

/// diag(1, −1) — the mirror, D4's second E-block generator.
let private mirror : int[][] = [| [| 1; 0 |]; [| 0; -1 |] |]

let private one1 (v: int) : int[][] = [| [| v |] |]

/// C4 = ⟨r | r⁴⟩, order 4. Three ℝ-irreducible labels: the two characters
/// A (r ↦ 1) and B (r ↦ −1), and the 2-dimensional E = the real form of the
/// conjugate pair {i, −i}, which is why E is of COMPLEX type and carries
/// J = R₉₀. ℝ-Burnside: 1 + 1 + 4/2 = 4.
let private c4 : PointGroup = {
    Name = "C4"
    Order = 4
    GenNames = [ "r" ]
    Irreps =
        [ { Name = "A"; DimR = 1; Fs = FsReal; Gens = [ one1 1 ]; J = None }
          { Name = "B"; DimR = 1; Fs = FsReal; Gens = [ one1 -1 ]; J = None }
          { Name = "E"; DimR = 2; Fs = FsComplex; Gens = [ r90 ]; J = Some r90 } ]
}

/// D4 = ⟨r, s | r⁴, s², (sr)²⟩, order 8. Four characters and one 2-dimensional
/// E, which has the SAME R₉₀ rotation generator as C4's E but is of REAL type:
/// the added mirror kills the complex structure (nothing commutes with both
/// R₉₀ and diag(1, −1) except the scalars). ℝ-Burnside: 1 + 1 + 1 + 1 + 4 = 8.
let private d4 : PointGroup = {
    Name = "D4"
    Order = 8
    GenNames = [ "r"; "s" ]
    Irreps =
        [ { Name = "A1"; DimR = 1; Fs = FsReal; Gens = [ one1 1; one1 1 ]; J = None }
          { Name = "A2"; DimR = 1; Fs = FsReal; Gens = [ one1 1; one1 -1 ]; J = None }
          { Name = "B1"; DimR = 1; Fs = FsReal; Gens = [ one1 -1; one1 1 ]; J = None }
          { Name = "B2"; DimR = 1; Fs = FsReal; Gens = [ one1 -1; one1 -1 ]; J = None }
          { Name = "E"; DimR = 2; Fs = FsReal; Gens = [ r90; mirror ]; J = None } ]
}

let private rawRegistry : PointGroup list = [ c4; d4 ]

/// The registered group names, in registry order.
let pointGroupNames : string list = rawRegistry |> List.map (fun g -> g.Name)

// ---------------------------------------------------------------------------
// Closure enumeration — the common word set
// ---------------------------------------------------------------------------

/// A hard cap on the BFS, so a malformed table (say a generator of infinite
/// order) fails loudly instead of hanging. No shipped group is near it.
let private closureCap = 256

/// Enumerate the group by BFS over the generators, an element being identified
/// by its TUPLE of matrices across all labels. Breadth-first from the identity
/// with right-multiplication, so the word attached to each element is a
/// shortest one and the enumeration order is stable. `certifyPointGroup` is
/// what pins the resulting count to the declared order.
let groupElements (grp: PointGroup) : PgElement list =
    let nGen = List.length grp.GenNames
    grp.Irreps
    |> List.iter (fun ir ->
        if List.length ir.Gens <> nGen then
            failwithf "internal: point group %s declares %d generator(s) %A but label %s carries %d generator matrices — the common word set is not well defined"
                grp.Name nGen grp.GenNames ir.Name (List.length ir.Gens))
    let ident = grp.Irreps |> List.map (fun ir -> matId ir.DimR)
    let seen = HashSet<string>()
    let out = ResizeArray<PgElement>()
    let queue = Queue<PgElement>()
    let e0 = { Word = []; Mats = ident }
    seen.Add(tupleKey ident) |> ignore
    out.Add e0
    queue.Enqueue e0
    while queue.Count > 0 do
        let cur = queue.Dequeue()
        for gi in 0 .. nGen - 1 do
            let nxt =
                List.map2 (fun (m: int[][]) (ir: PgIrrep) -> matMul m (List.item gi ir.Gens))
                    cur.Mats grp.Irreps
            let k = tupleKey nxt
            if seen.Add k then
                if out.Count >= closureCap then
                    failwithf "internal: the generator closure of point group %s exceeded %d elements (declared order %d) — a generator matrix is not of finite order"
                        grp.Name closureCap grp.Order
                let el = { Word = cur.Word @ [ gi ]; Mats = nxt }
                out.Add el
                queue.Enqueue el
    List.ofSeq out

/// A word rendered with the group's generator names ("e" for the identity).
let wordName (grp: PointGroup) (w: int list) : string =
    if List.isEmpty w then "e"
    else w |> List.map (fun gi -> List.item gi grp.GenNames) |> String.concat ""

// ---------------------------------------------------------------------------
// The FS indicator
// ---------------------------------------------------------------------------

/// The Frobenius–Schur indicator of a label, computed from the enumerated
/// elements: ν(U) = Σ_g χ_U(g²) / |G|, an exact integer (the division is
/// asserted exact by `certifyPointGroup`).
///
/// THE VALUE CONVENTION, because it is a genuine trap. χ_U here is the
/// character of the ℝ-IRREDUCIBLE label, not of a ℂ-irreducible constituent,
/// and the two conventions disagree at quaternionic type:
///
///   e = 1 (ℝ): U ⊗ ℂ is ℂ-irreducible with indicator +1        ⇒ ν = +1
///   e = 2 (ℂ): U ⊗ ℂ = W ⊕ W̄, indicators 0 + 0                 ⇒ ν =  0
///   e = 4 (ℍ): U ⊗ ℂ = W ⊕ W, indicators (−1) + (−1)           ⇒ ν = −2
///
/// i.e. ν = 2 − e exactly, which is the identity `certifyPointGroup` asserts.
/// The textbook 1 / 0 / −1 triple is the ℂ-irreducible reading; on everything
/// this registry ships (real and complex type) the two agree, and the ℍ row is
/// unreachable in a single point group anyway. Stating it as 2 − e keeps the
/// assert exact rather than approximately right.
let fsIndicator (grp: PointGroup) (els: PgElement list) (irrepIndex: int) : int =
    let total =
        els |> List.sumBy (fun el ->
            let m = List.item irrepIndex el.Mats
            matTrace (matMul m m))
    if total % grp.Order <> 0 then
        failwithf "internal: the FS indicator of %s::%s is %d/%d, not an integer — the element enumeration is wrong"
            grp.Name (List.item irrepIndex grp.Irreps).Name total grp.Order
    total / grp.Order

/// Σᵢ dᵢ²/eᵢ — the ℝ-Burnside sum, which must equal |G|.
let burnsideSum (grp: PointGroup) : int =
    grp.Irreps
    |> List.sumBy (fun ir ->
        let e = endDim ir.Fs
        if (ir.DimR * ir.DimR) % e <> 0 then
            failwithf "internal: label %s::%s has d = %d and e = %d, but e does not divide d^2 — no Wedderburn block has that shape"
                grp.Name ir.Name ir.DimR e
        (ir.DimR * ir.DimR) / e)

// ---------------------------------------------------------------------------
// The integrity certificate
// ---------------------------------------------------------------------------

/// What `certifyPointGroup` computed on the way to passing. Returned rather
/// than discarded so the test block can PRINT the numbers (a certificate you
/// cannot read is a certificate you cannot review).
type PgIntegrity = {
    Group: string
    Order: int
    Closure: int
    /// (label, ν) per label, in table order.
    FsIndicators: (string * int) list
    /// Σ dᵢ²/eᵢ.
    Burnside: int
    /// The labels whose J identities were verified.
    JLabels: string list
    /// The enumerated elements, as words.
    Words: string list
}

/// The six integrity families of the header, all integer-exact. failwithf on
/// any violation: these are compiler bugs, not user errors.
let certifyPointGroup (grp: PointGroup) : PgIntegrity =
    // (1) SHAPE
    if grp.Order < 1 then
        failwithf "internal: point group %s declares order %d" grp.Name grp.Order
    if List.isEmpty grp.Irreps then
        failwithf "internal: point group %s has no labels" grp.Name
    let names = grp.Irreps |> List.map (fun ir -> ir.Name)
    if List.length (List.distinct names) <> List.length names then
        failwithf "internal: point group %s has duplicate label names %A" grp.Name names
    grp.Irreps
    |> List.iter (fun ir ->
        if ir.DimR < 1 then
            failwithf "internal: label %s::%s has dimension %d" grp.Name ir.Name ir.DimR
        ir.Gens
        |> List.iteri (fun gi m ->
            if not (matIsSquare ir.DimR m) then
                failwithf "internal: generator %s of %s::%s is not %d x %d"
                    (List.item gi grp.GenNames) grp.Name ir.Name ir.DimR ir.DimR)
        match ir.J with
        | Some j when not (matIsSquare ir.DimR j) ->
            failwithf "internal: J of %s::%s is not %d x %d" grp.Name ir.Name ir.DimR ir.DimR
        | _ -> ())

    // (2) CLOSURE — count and multiplicative closure over the common word set.
    let els = groupElements grp
    let closure = List.length els
    if closure <> grp.Order then
        failwithf "internal: the generator closure of point group %s has %d elements but the table declares order %d — the generator matrices do not generate the declared group"
            grp.Name closure grp.Order
    let keys = HashSet<string>(els |> List.map (fun el -> tupleKey el.Mats))
    for a in els do
        for b in els do
            let prod = List.map2 matMul a.Mats b.Mats
            if not (keys.Contains(tupleKey prod)) then
                failwithf "internal: the enumerated elements of %s are not closed under multiplication (%s * %s escapes)"
                    grp.Name (wordName grp a.Word) (wordName grp b.Word)

    // (3) ORTHOGONALITY — what makes the oracle's ρ(g)^T = ρ(g)^-1 legitimate.
    for el in els do
        List.iter2 (fun (m: int[][]) (ir: PgIrrep) ->
            if not (matEq (matMul (matTranspose m) m) (matId ir.DimR)) then
                failwithf "internal: the matrix of %s::%s at element %s is not orthogonal — the group-average form rho_W(g) M rho_V(g)^T is not the Reynolds operator for this table"
                    grp.Name ir.Name (wordName grp el.Word)) el.Mats grp.Irreps

    // (4) THE FS INDICATOR — nu = 2 - e, exactly (see `fsIndicator`).
    let indicators =
        grp.Irreps
        |> List.mapi (fun i ir ->
            let nu = fsIndicator grp els i
            let want = 2 - endDim ir.Fs
            if nu <> want then
                failwithf "internal: label %s::%s declares Fs = %s (e = %d), so the FS indicator sum_g chi(g^2)/|G| must be %d, but the element enumeration gives %d"
                    grp.Name ir.Name (fsName ir.Fs) (endDim ir.Fs) want nu
            (ir.Name, nu))

    // (5) THE J IDENTITIES, and the declared-Fs/declared-J agreement.
    let jLabels =
        grp.Irreps
        |> List.choose (fun ir ->
            match ir.Fs, ir.J with
            | FsComplex, None ->
                failwithf "internal: label %s::%s is of complex type (e = 2) but carries no baked J — the emitted End-basis [Id, J] has nothing to emit"
                    grp.Name ir.Name
            | FsReal, Some _ ->
                failwithf "internal: label %s::%s is of real type (e = 1) but carries a baked J — End is one-dimensional, so a J would be a scalar with J^2 = -Id"
                    grp.Name ir.Name
            | FsQuat, _ ->
                failwithf "internal: label %s::%s declares FsQuat — the value is RESERVED for double groups (plan-transforms-as-types §3.6); no single point group has a quaternionic label"
                    grp.Name ir.Name
            | _, None -> None
            | _, Some j ->
                if not (matEq (matMul j j) (matNeg (matId ir.DimR))) then
                    failwithf "internal: J^2 <> -Id for %s::%s" grp.Name ir.Name
                ir.Gens
                |> List.iteri (fun gi g ->
                    if not (matEq (matMul j g) (matMul g j)) then
                        failwithf "internal: J does not commute with generator %s of %s::%s — J is not an endomorphism of the label"
                            (List.item gi grp.GenNames) grp.Name ir.Name)
                Some ir.Name)

    // (6) THE R-BURNSIDE TRAP.
    let bs = burnsideSum grp
    if bs <> grp.Order then
        failwithf "internal: the R-Burnside sum of %s is sum_i d_i^2/e_i = %d, but |G| = %d — the label list is incomplete, or a dimension or an FS type is wrong"
            grp.Name bs grp.Order

    { Group = grp.Name
      Order = grp.Order
      Closure = closure
      FsIndicators = indicators
      Burnside = bs
      JLabels = jLabels
      Words = els |> List.map (fun el -> wordName grp el.Word) }

// ---------------------------------------------------------------------------
// The registry
// ---------------------------------------------------------------------------

let private certified = Dictionary<string, PointGroup>()

/// Fetch a registered point group BY NAME, running the integrity certificate
/// the first time (memoized: the tables are immutable, so once is enough, and
/// a violation is a hard failure rather than a cached one).
let pointGroup (name: string) : PointGroup =
    match certified.TryGetValue name with
    | true, g -> g
    | _ ->
        match rawRegistry |> List.tryFind (fun g -> g.Name = name) with
        | None ->
            failwithf "internal: unknown point group '%s' — the registry is {%s}"
                name (String.concat ", " pointGroupNames)
        | Some g ->
            certifyPointGroup g |> ignore
            certified.[name] <- g
            g

/// Look a label up in a group. This is the unknown-label failure point of the
/// counting layer; the SOURCE-level decoder diagnostic (`pgSpecOfStatic`) is
/// 5b-i's and does not route through here.
let pgIrrep (grp: PointGroup) (label: string) : PgIrrep =
    match grp.Irreps |> List.tryFind (fun ir -> ir.Name = label) with
    | Some ir -> ir
    | None ->
        failwithf "internal: point group %s has no label '%s' — its labels are {%s}"
            grp.Name label (grp.Irreps |> List.map (fun ir -> ir.Name) |> String.concat ", ")

/// The index of a label in the group's table order — the index into a
/// `PgElement.Mats` tuple.
let pgIrrepIndex (grp: PointGroup) (label: string) : int =
    match grp.Irreps |> List.tryFindIndex (fun ir -> ir.Name = label) with
    | Some i -> i
    | None ->
        failwithf "internal: point group %s has no label '%s' — its labels are {%s}"
            grp.Name label (grp.Irreps |> List.map (fun ir -> ir.Name) |> String.concat ", ")

/// The EMITTED End-basis of a cell: [Id] at real type, [Id, J] at complex
/// type. Its length is `endDim ir.Fs` by construction, which is the bridge
/// between the count and the emission — the FS formula's e_i is literally this
/// list's length.
///
/// This is the emission-adjacent path the header reserves FsQuat against: the
/// counting core reads `endDim FsQuat = 4` happily, and this function refuses,
/// so a quaternionic label can be counted but never emitted.
let endBasis (ir: PgIrrep) : int[][] list =
    match ir.Fs with
    | FsReal -> [ matId ir.DimR ]
    | FsComplex ->
        match ir.J with
        | Some j -> [ matId ir.DimR; j ]
        | None ->
            failwithf "internal: label %s is of complex type but has no baked J" ir.Name
    | FsQuat ->
        failwithf "internal: label %s is of quaternionic type — FsQuat is a RESERVED counting value (plan-transforms-as-types §3.6: counts are uniform in e, emission is not). The [1, i, j, k] End-basis of a double-group label has no baked table here and no emitter asking for one"
            ir.Name

// ---------------------------------------------------------------------------
// THE GENERIC e-WEIGHTED COUNTING CORE
//
// Parameterized over the LABEL type alone: a block algebra says how big a
// label is and how big its endomorphism ring is, and everything below is
// integer arithmetic over `('K * int) list` specs. Instantiated twice in this
// arc — at 'K = string (point-group labels, below) and, in the test block only,
// at 'K = (l, parity) with EndDim ≡ 1, where it must agree with MLSpec.homDim
// and MLSpec.homBlocks on a spec sweep. That pin is the whole of §3.6's
// "twin-not-reroute": the abstraction is DEMONSTRATED against the O(3) member
// without touching it.
// ---------------------------------------------------------------------------

/// How a family of labels sizes: the ℝ-dimension of a label and the
/// ℝ-dimension of its endomorphism ring.
type BlockAlgebra<'K when 'K : comparison> = {
    Dim: 'K -> int
    EndDim: 'K -> int
}

/// Σᵢ mᵢ·dim(Uᵢ) — the ℝ-dimension of the module a spec describes.
let genericTotalDim (alg: BlockAlgebra<'K>) (spec: ('K * int) list) : int =
    spec |> List.sumBy (fun (k, m) -> m * alg.Dim k)

/// Total multiplicity per label. Duplicate spec entries AGGREGATE, matching
/// MLSpec.aggregateByIrrep — a spec is an ordered list of blocks, but the Hom
/// space only sees how many copies of each label there are in total.
let genericAggregate (spec: ('K * int) list) : Map<'K, int> =
    (Map.empty, spec) ||> List.fold (fun acc (k, m) ->
        acc |> Map.change k (fun cur -> Some (defaultArg cur 0 + m)))

/// THE FS FORMULA — stated once, here, and referenced everywhere else.
///
/// Over ℝ-irreducible labels U_i with e_i = dim_ℝ End_G(U_i) ∈ {1 (ℝ), 2 (ℂ),
/// 4 (ℍ)}:
///
///     dim_ℝ Hom_G(⊕ᵢ mᵢ·Uᵢ, ⊕ᵢ nᵢ·Uᵢ) = Σᵢ mᵢ·nᵢ·eᵢ
///
/// Each multiplicity cell carries e_i scalars, not one: Schur over ℝ gives an
/// element of the division algebra End_G(U_i), and the emitted basis of a cell
/// is [Id] at e = 1 and [Id, J] at e = 2 (`endBasis`). At e_i ≡ 1 — which is
/// every O(3) label, and every label of D4 — the formula degenerates to the
/// familiar Σ mᵢ·nᵢ of MLSpec.homDim, which is why the correction is invisible
/// until a second family arrives.
let genericHomDim (alg: BlockAlgebra<'K>) (specIn: ('K * int) list) (specOut: ('K * int) list) : int =
    let aIn = genericAggregate specIn
    genericAggregate specOut
    |> Map.fold (fun acc k mOut ->
        match Map.tryFind k aIn with
        | Some mIn -> acc + mIn * mOut * alg.EndDim k
        | None -> acc) 0

/// The Hom basis as BLOCK PAIRS: every (input block, output block) pair sharing
/// a label, output-major, exactly mirroring MLSpec.homBlocks (all pairs, not
/// first-match; an output block with no matching input is simply absent). Each
/// pair contributes mOut·mIn CELLS and each cell e scalars, so
/// `Σ_pairs mOut·mIn·e = genericHomDim` — the identity the test block pins.
///
/// Yields (inputBlockIndex, outputBlockIndex, outputEntry, inputEntry), the
/// MLSpec.homBlocks tuple order.
let genericHomBlocks (alg: BlockAlgebra<'K>) (specIn: ('K * int) list) (specOut: ('K * int) list)
                     : (int * int * ('K * int) * ('K * int)) list =
    ignore alg
    let sIn = List.toArray specIn
    let sOut = List.toArray specOut
    [ for bo in 0 .. sOut.Length - 1 do
        for bi in 0 .. sIn.Length - 1 do
          if fst sIn.[bi] = fst sOut.[bo] then
            yield (bi, bo, sOut.[bo], sIn.[bi]) ]

// ---------------------------------------------------------------------------
// The point-group instantiation
// ---------------------------------------------------------------------------

/// A point-group spec: (LABEL_NAME, multiplicity) entries against a group, in
/// block order. Names rather than indices because §3.6 fixed the surface
/// encoding that way — `SVString` is already first-class in StaticEval, so the
/// name surface costs no new static machinery, and the frozen table names are
/// the diagnostic identity.
type PgSpec = (string * int) list

/// The block algebra of a point group: label ↦ (DimR, e). Every `pg*` function
/// below is the generic core at this instantiation and nothing else.
let pgAlgebra (grp: PointGroup) : BlockAlgebra<string> =
    { Dim = fun label -> (pgIrrep grp label).DimR
      EndDim = fun label -> endDim (pgIrrep grp label).Fs }

let private checkSpec (grp: PointGroup) (spec: PgSpec) : unit =
    spec |> List.iter (fun (label, m) ->
        pgIrrep grp label |> ignore
        if m < 0 then
            failwithf "internal: spec entry %s::%s has negative multiplicity %d" grp.Name label m)

/// dim_ℝ of the module a pg spec describes.
let pgTotalDim (grp: PointGroup) (spec: PgSpec) : int =
    checkSpec grp spec
    genericTotalDim (pgAlgebra grp) spec

/// dim_ℝ Hom_G(V_in, V_out) for a point group — the FS formula at
/// `genericHomDim`, with e read from the frozen table.
///
/// THE CONTRAST, in one line: on the spec shape [trivial × 1, E × 2] → itself
/// this is 1 + 2·2·2 = 9 at C4 and 1 + 2·2·1 = 5 at D4. Same dimensions, same
/// R₉₀ generator, same multiplicities; the FS type of E is the only input that
/// differs.
let pgHomDim (grp: PointGroup) (specIn: PgSpec) (specOut: PgSpec) : int =
    checkSpec grp specIn
    checkSpec grp specOut
    genericHomDim (pgAlgebra grp) specIn specOut

/// The pg Hom basis as block pairs — `genericHomBlocks` at the pg
/// instantiation. Each pair (bi, bo, (label, mOut), (label, mIn)) carries
/// mOut·mIn cells of `endBasis` columns apiece.
let pgHomBlocks (grp: PointGroup) (specIn: PgSpec) (specOut: PgSpec)
                : (int * int * (string * int) * (string * int)) list =
    checkSpec grp specIn
    checkSpec grp specOut
    genericHomBlocks (pgAlgebra grp) specIn specOut

/// Block start offsets in the ℝ-module of a spec; length = spec length + 1,
/// last = pgTotalDim. The emitted-basis layout of 5b-i, and what the 5b-0
/// oracle uses to place a cell's End-basis matrix inside Hom(V, W).
let pgBlockStarts (grp: PointGroup) (spec: PgSpec) : int list =
    checkSpec grp spec
    let alg = pgAlgebra grp
    (0, spec) ||> List.scan (fun acc (k, m) -> acc + m * alg.Dim k)

/// ρ_spec(g) — the BLOCK-DIAGONAL matrix by which an element acts on the
/// ℝ-module a spec describes, in the emitted layout: block b holds its
/// `mult` copies CONSECUTIVELY, each a full DimR-sized copy of the label's
/// matrix at that element. (Copy-major, which is the layout corpus 045 pins
/// from the value side: at [A1 × 1, E × 2] the rotation sends
/// [x0, x1, x2, x3, x4] to [x0, −x2, x1, −x4, x3].)
///
/// Integer in, integer out — every shipped generator entry lies in {0, ±1}, so
/// no coefficient ring is needed to consume this. Stage 6b's finite discharge
/// is its first caller; the 5b-0 oracle builds its own Reynolds sums from
/// `groupElements` directly and is untouched.
let pgElementMatrix (grp: PointGroup) (spec: PgSpec) (el: PgElement) : int[][] =
    checkSpec grp spec
    let n = pgTotalDim grp spec
    let out = Array.init n (fun _ -> Array.zeroCreate n)
    let starts = pgBlockStarts grp spec
    spec
    |> List.iteri (fun b (label, mult) ->
        let ir = pgIrrep grp label
        let m = List.item (pgIrrepIndex grp label) el.Mats
        let d = ir.DimR
        for c in 0 .. mult - 1 do
            let off = List.item b starts + c * d
            for i in 0 .. d - 1 do
                for j in 0 .. d - 1 do
                    out.[off + i].[off + j] <- m.[i].[j])
    out
