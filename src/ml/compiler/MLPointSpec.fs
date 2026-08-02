/// Point groups — the second BLOCK-SPEC member of the transforms-as-types
/// discipline (retired transforms-as-types plan §3.6's 5b subsection, §7 stage 5,
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
                failwithf "internal: label %s::%s declares FsQuat — the value is RESERVED for double groups (retired transforms-as-types plan §3.6); no single point group has a quaternionic label"
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
        failwithf "internal: label %s is of quaternionic type — FsQuat is a RESERVED counting value (retired transforms-as-types plan §3.6: counts are uniform in e, emission is not). The [1, i, j, k] End-basis of a double-group label has no baked table here and no emitter asking for one"
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

// ---------------------------------------------------------------------------
// RESTRICTION FROM O(3) — THE BRANCHING RULES
// (retired equivariance-in-types plan stage A3; the "named-not-shipped"
// promise of the retired transforms-as-types plan §3.6, now shipped as a TABLE.)
// ---------------------------------------------------------------------------
//
// WHY AN EMBEDDING IS NEW DATA, AND NOT DERIVABLE FROM ANYTHING ABOVE
// -------------------------------------------------------------------
// Everything before this line is ABSTRACT: a label is a tuple of matrices in
// its own basis, and NOTHING in the registry says which orthogonal
// transformation of ℝ³ the generator `r` IS. A restriction is not a fact about
// an abstract group — D^l|_G depends on HOW G sits inside O(3), and the choice
// is real: the same abstract D4 restricts differently as the proper rotation
// group 422 (second generator a two-fold AXIS) and as C4v (second generator a
// MIRROR), because those two differ by the inversion, which O(3) irreps see
// through their parity label. So the embedding is DECLARED here, beside the
// tables it interprets, and certified as a faithful homomorphism.
//
// THE SHIPPED EMBEDDINGS ARE PROPER, AND THAT DECIDES PARITY
// ----------------------------------------------------------
//   C4 -> ⟨R_z(90°)⟩ ⊂ SO(3)                (the four-fold axis is z)
//   D4 -> ⟨R_z(90°), R_x(180°)⟩ ⊂ SO(3)     (the crystallographic 422)
//
// Both land in SO(3), so −I ∉ G, so THE O(3) PARITY LABEL DOES NOT ENTER THE
// CHARACTER COMPUTATION: D^(l, even)|_G and D^(l, odd)|_G are the same
// point-group module. That is not an omission — it is the content of
// restricting to a proper subgroup, and it is a STRENGTH LOSS worth stating
// out loud: an (l = 0, ODD) pseudoscalar restricts to the TRIVIAL label, i.e.
// becomes a genuine G-invariant, because no element of G is improper and
// nothing left in the group can detect the sign flip. `o3Character`'s improper
// branch is written and, on this roster, unreachable; it is the one line that
// starts firing the moment an improper embedding (C4v, D4h) is registered, and
// it is where the parity label re-enters.
//
// THE FROBENIUS–SCHUR CORRECTION, AGAIN, IN A SECOND PLACE
// --------------------------------------------------------
// Over ℝ the multiplicity is NOT the character inner product:
//
//     ⟨χ_U, χ_U⟩ = dim_ℝ Hom_G(U, U) = dim_ℝ End_G(U) = e_U
//     ⟨χ_V, χ_U⟩ = dim_ℝ Hom_G(V, U) = m_U · e_U   ⇒   m_U = ⟨χ_V, χ_U⟩ / e_U
//
// so the same e that corrects `genericHomDim` corrects the branching. Dropping
// it is not a subtle error: at C4 (E of complex type, e = 2) the uncorrected
// count doubles E, and l = 1 would "decompose" into 5 dimensions of a
// 3-dimensional space. The dimension-closure assert below catches exactly that.
//
// EVERYTHING IS EXACT INTEGER ARITHMETIC — no angles, no floats, no
// sin((2l+1)θ/2)/sin(θ/2). `chiRot` runs the Clebsch–Gordan recurrence
// χ_1·χ_l = χ_(l−1) + χ_l + χ_(l+1) on the integer t = tr(R), which is the same
// statement one algebraic step earlier and never asks what θ is.
//
// WHAT THIS IS NOT: A REINTERPRETATION OF A BUFFER
// ------------------------------------------------
// `restrictSpec` names the DECOMPOSITION. It does NOT say that an
// `IrrepsIdx<S>` buffer is a `PgIrrepsIdx<g, restrictSpec S>` buffer, and it is
// not: the two LAYOUTS disagree. The O(3) layout orders a block's components by
// m = −l..l, so the G-invariant m = 0 component sits in the MIDDLE (index l)
// with the m-pairs straddling it at l ± k; a pg spec lays each label's copies
// out CONSECUTIVELY (`pgBlockStarts`). Already at l = 1 under C4 the invariant
// direction is at index 1 on the O(3) side and at index 0 on the pg side, and
// no ORDERING of the restricted spec can fix it, because the E pair is SPLIT by
// the A component while pg blocks are contiguous by construction. The change of
// basis is therefore a genuine permutation (with signs, since an m-pair block
// need not carry the table's chosen orientation of R₉₀), which is why stage A3
// ships this table and NOT a type-level identity view. A value-level
// `ml.restrict` would have to emit that permutation.

/// The geometric embedding of a registered point group into O(3): what each
/// generator IS, as a 3×3 orthogonal integer matrix. `GenMats` is PARALLEL to
/// the group's `GenNames`, exactly as every label's `Gens` is.
type PgEmbedding = {
    Group: string
    GenMats: int[][] list
    /// What each generator is, in words. Diagnostics and review only — but it
    /// is the reviewable half of the convention, so it is not optional.
    GenWhat: string list
}

/// R_z(90°) — the four-fold axis of both shipped groups, in the standard
/// (x, y, z) Cartesian basis.
let private rotZ90 : int[][] = [| [| 0; -1; 0 |]; [| 1; 0; 0 |]; [| 0; 0; 1 |] |]

/// R_x(180°) = diag(1, −1, −1) — a two-fold AXIS along x, not a mirror. This
/// single choice is what makes the shipped D4 the proper group 422 rather than
/// C4v, and it is also what fixes the B1/B2 label assignment (with the two-fold
/// axis along x, the abstract table's B1 — r ↦ −1, s ↦ +1 — is the x²−y²
/// direction and B2 is xy; along a face diagonal the two swap). Nothing below
/// depends on which of the two it is EXCEPT that assignment: the branching
/// multiset is the same either way.
let private rotX180 : int[][] = [| [| 1; 0; 0 |]; [| 0; -1; 0 |]; [| 0; 0; -1 |] |]

let private embeddings : PgEmbedding list =
    [ { Group = "C4"; GenMats = [ rotZ90 ]; GenWhat = [ "R_z(90 deg)" ] }
      { Group = "D4"; GenMats = [ rotZ90; rotX180 ]; GenWhat = [ "R_z(90 deg)"; "R_x(180 deg)" ] } ]

/// The embedding of a registered group. Unregistered = compiler bug, exactly
/// like `pointGroup`: a group in the registry with no embedding is a group
/// whose restriction is undefined, and the two lists are meant to stay the same
/// length.
let pgEmbedding (name: string) : PgEmbedding =
    match embeddings |> List.tryFind (fun e -> e.Group = name) with
    | Some e -> e
    | None ->
        failwithf "internal: point group '%s' has no declared O(3) embedding — every registered group needs one before its restriction is defined (the registry is {%s})"
            name (String.concat ", " pointGroupNames)

/// det of a 3×3 integer matrix.
let private det3 (m: int[][]) : int =
    m.[0].[0] * (m.[1].[1] * m.[2].[2] - m.[1].[2] * m.[2].[1])
    - m.[0].[1] * (m.[1].[0] * m.[2].[2] - m.[1].[2] * m.[2].[0])
    + m.[0].[2] * (m.[1].[0] * m.[2].[1] - m.[1].[1] * m.[2].[0])

let private geoCache = Dictionary<string, (PgElement * int[][]) list>()

/// The group's elements paired with the 3×3 matrix each one IS in ℝ³ — the
/// bridge between the abstract table and O(3), CERTIFIED on first use:
///
///   1. every generator matrix is 3×3 and orthogonal (MᵀM = Id);
///   2. the word map is MULTIPLICATIVE — for every pair of elements the
///      geometric matrix of their abstract product is the product of their
///      geometric matrices. This is what makes "evaluate the element's word in
///      the geometric generators" a well-defined homomorphism rather than a
///      property of the particular word the BFS happened to hand out;
///   3. the map is FAITHFUL — |image| = |G|. A non-faithful embedding embeds a
///      QUOTIENT, and restricting along it would decompose the wrong group
///      under the right group's labels.
let embeddedElements (grp: PointGroup) : (PgElement * int[][]) list =
    match geoCache.TryGetValue grp.Name with
    | true, v -> v
    | _ ->
        let emb = pgEmbedding grp.Name
        if List.length emb.GenMats <> List.length grp.GenNames then
            failwithf "internal: the O(3) embedding of %s declares %d generator matrices but the group has %d generators %A"
                grp.Name (List.length emb.GenMats) (List.length grp.GenNames) grp.GenNames
        emb.GenMats
        |> List.iteri (fun gi m ->
            if not (matIsSquare 3 m) then
                failwithf "internal: the O(3) embedding of generator %s of %s is not 3 x 3"
                    (List.item gi grp.GenNames) grp.Name
            if not (matEq (matMul (matTranspose m) m) (matId 3)) then
                failwithf "internal: the O(3) embedding of generator %s of %s is not orthogonal — it is not an element of O(3)"
                    (List.item gi grp.GenNames) grp.Name)
        let els = groupElements grp
        let geoOf (w: int list) =
            w |> List.fold (fun acc gi -> matMul acc (List.item gi emb.GenMats)) (matId 3)
        let pairs = els |> List.map (fun el -> (el, geoOf el.Word))
        // (2) MULTIPLICATIVITY
        let byAbs = Dictionary<string, int[][]>()
        for (el, g) in pairs do byAbs.[tupleKey el.Mats] <- g
        for (a, ga) in pairs do
            for (b, gb) in pairs do
                let prodKey = tupleKey (List.map2 matMul a.Mats b.Mats)
                match byAbs.TryGetValue prodKey with
                | true, gp ->
                    if not (matEq gp (matMul ga gb)) then
                        failwithf "internal: the O(3) embedding of %s is not a homomorphism — the geometric matrix of %s * %s is not the product of their geometric matrices"
                            grp.Name (wordName grp a.Word) (wordName grp b.Word)
                | _ ->
                    failwithf "internal: the enumerated elements of %s are not closed under multiplication at the embedding step (%s * %s)"
                        grp.Name (wordName grp a.Word) (wordName grp b.Word)
        // (3) FAITHFULNESS
        let images = pairs |> List.map (fun (_, g) -> tupleKey [ g ]) |> List.distinct
        if List.length images <> grp.Order then
            failwithf "internal: the O(3) embedding of %s has image of size %d but |G| = %d — it embeds a QUOTIENT, and restricting along it would decompose the wrong group"
                grp.Name (List.length images) grp.Order
        geoCache.[grp.Name] <- pairs
        pairs

/// χ_l of a PROPER rotation whose 3×3 trace is t, by the Clebsch–Gordan
/// recurrence χ_1·χ_l = χ_(l−1) + χ_l + χ_(l+1), i.e.
/// χ_(l+1) = (t − 1)·χ_l − χ_(l−1) with χ_0 = 1 and χ_1 = t = 1 + 2cos θ.
/// Exact in t; no angle is ever named, so there is no float and no half-angle
/// convention to get wrong.
let private chiRot (l: int) (t: int) : int =
    if l < 0 then failwithf "internal: MLPointSpec.chiRot negative l (%d)" l
    if l = 0 then 1
    else
        let mutable prev = 1
        let mutable cur = t
        for _ in 2 .. l do
            let nxt = (t - 1) * cur - prev
            prev <- cur
            cur <- nxt
        cur

/// χ of the O(3) irrep (l, parity) at an element of O(3) given as a 3×3
/// orthogonal integer matrix. O(3) ≅ SO(3) × {±I}, so an improper g factors as
/// (−I)·R with R = −g proper, and ρ_(l,p)(−I) = (−1)^p·Id — which is the whole
/// of the parity handling, and is DEAD CODE on the shipped (proper) roster.
let o3Character (l: int) (parity: int) (g: int[][]) : int =
    if not (matIsSquare 3 g) then
        failwithf "internal: MLPointSpec.o3Character expects a 3 x 3 matrix"
    if parity <> 0 && parity <> 1 then
        failwithf "internal: MLPointSpec.o3Character parity must be 0 (even) or 1 (odd), got %d" parity
    let d = det3 g
    if d <> 1 && d <> -1 then
        failwithf "internal: MLPointSpec.o3Character was handed a matrix of determinant %d — not an element of O(3)" d
    let chi = chiRot l (matTrace (if d = 1 then g else matNeg g))
    if d = 1 || parity = 0 then chi else -chi

let private restrictCache = Dictionary<string * int * int, PgSpec>()

/// THE BRANCHING RULE for ONE O(3) irrep: D^(l, parity) restricted along the
/// declared embedding, as multiplicities over the group's own labels, in TABLE
/// order and with zero multiplicities dropped.
///
///     m_μ = ( (1/|G|)·Σ_g χ_(l,p)(g)·χ_μ(g) ) / e_μ
///
/// (real characters, so the conjugation in the textbook formula is the
/// identity; e_μ is the Frobenius–Schur correction of the header).
///
/// THREE ASSERTS, all integer-exact, all compiler-bug guards:
///   * the inner product is divisible by |G| and then by e_μ — a non-integer
///     multiplicity means the embedding, the table or the character recurrence
///     is wrong, and no user input can cause it;
///   * DIMENSION CLOSURE, Σ m_μ·d_μ = 2l+1 — the trap that catches a dropped FS
///     correction (it doubles a complex-type label and overshoots) and a
///     missing label;
///   * THE CHARACTER CROSS-CHECK, at EVERY element: the trace of
///     `pgElementMatrix` on the RESULT must equal χ_(l,p) there. This is the
///     numerical guard against convention drift, and it deliberately routes
///     through the emitted LAYOUT rather than re-summing multiplicities, so the
///     block-diagonal assembly the rest of the pipeline consumes is what gets
///     checked rather than the arithmetic that produced it.
let restrictIrrep (grp: PointGroup) (l: int) (parity: int) : PgSpec =
    if l < 0 then failwithf "internal: MLPointSpec.restrictIrrep negative l (%d)" l
    if parity <> 0 && parity <> 1 then
        failwithf "internal: MLPointSpec.restrictIrrep parity must be 0 or 1, got %d" parity
    match restrictCache.TryGetValue ((grp.Name, l, parity)) with
    | true, v -> v
    | _ ->
        let pairs = embeddedElements grp
        let spec =
            grp.Irreps
            |> List.mapi (fun i ir ->
                let e = endDim ir.Fs
                let total =
                    pairs
                    |> List.sumBy (fun (el, g) ->
                        o3Character l parity g * matTrace (List.item i el.Mats))
                if total % grp.Order <> 0 then
                    failwithf "internal: the character inner product of D^(l=%d, parity=%d) restricted to %s against label %s is %d/%d, not an integer"
                        l parity grp.Name ir.Name total grp.Order
                let ip = total / grp.Order
                if ip % e <> 0 then
                    failwithf "internal: <chi of (l=%d, parity=%d) restricted to %s, chi_%s> = %d is not divisible by e = %d — over R the multiplicity is the inner product DIVIDED by dim End, so a non-divisible value means the table's FS type is wrong"
                        l parity grp.Name ir.Name ip e
                if ip < 0 then
                    failwithf "internal: D^(l=%d, parity=%d) restricted to %s gives label %s NEGATIVE multiplicity %d"
                        l parity grp.Name ir.Name (ip / e)
                (ir.Name, ip / e))
            |> List.filter (fun (_, m) -> m > 0)
        let dimSum = spec |> List.sumBy (fun (nm, m) -> m * (pgIrrep grp nm).DimR)
        if dimSum <> 2 * l + 1 then
            failwithf "internal: D^(l=%d, parity=%d) restricted to %s decomposed to %d dimensions but the irrep has %d — a dropped Frobenius-Schur correction reads exactly like this"
                l parity grp.Name dimSum (2 * l + 1)
        for (el, g) in pairs do
            let want = o3Character l parity g
            let got = matTrace (pgElementMatrix grp spec el)
            if got <> want then
                failwithf "internal: the restriction of D^(l=%d, parity=%d) to %s reconstructs character %d at element %s, but the O(3) character there is %d — the branching table and the element action disagree"
                    l parity grp.Name got (wordName grp el.Word) want
        restrictCache.[(grp.Name, l, parity)] <- spec
        spec

/// THE BRANCHING RULE for a whole O(3) spec, given as raw (l, parity, mult)
/// triples. Raw triples rather than `MLSpec.Spec` on purpose: this module is
/// dependency-free of MLSpec by design (§3.6's twin-not-reroute discipline),
/// and the caller that has a `Spec` in hand — MLStatics — already opens both.
///
/// Multiplicities AGGREGATE across blocks (the `genericAggregate` rule: a spec
/// is an ordered list of blocks, but the restricted module only knows how many
/// copies of each label it holds in total), and the result is in the group's
/// TABLE order — the same canonicalization discipline `tpSpec` and `powerSpec`
/// use, so the answer is stable to write in an annotation.
let restrictSpec (grp: PointGroup) (entries: (int * int * int) list) : PgSpec =
    let acc =
        (Map.empty, entries)
        ||> List.fold (fun m (l, parity, mult) ->
            if mult < 1 then
                failwithf "internal: MLPointSpec.restrictSpec was handed a block of multiplicity %d" mult
            restrictIrrep grp l parity
            |> List.fold (fun m2 (nm, k) ->
                m2 |> Map.change nm (fun cur -> Some (defaultArg cur 0 + k * mult))) m)
    let out =
        grp.Irreps
        |> List.choose (fun ir ->
            match Map.tryFind ir.Name acc with
            | Some m when m > 0 -> Some (ir.Name, m)
            | _ -> None)
    let want = entries |> List.sumBy (fun (l, _, mult) -> mult * (2 * l + 1))
    let got = pgTotalDim grp out
    if got <> want then
        failwithf "internal: restricting a spec of dim %d to %s produced a module of dim %d" want grp.Name got
    out
