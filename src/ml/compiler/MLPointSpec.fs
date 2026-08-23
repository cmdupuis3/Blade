/// Point groups -- the second BLOCK-SPEC member of the transforms-as-types
/// discipline. This file is the COUNTING half: frozen integer character
/// tables for {C4, D4}, their load-time integrity certificate, the generic
/// e-weighted Hom counting core, and the `pg*` sizing wrappers. No emission,
/// no tag grammar, no certification lattice.
/// Dependency-free on purpose (not even MLSpec): the generic core below is a
/// TEST-PINNED TWIN of MLSpec.homDim/homBlocks, not a replacement for it.
/// MLSpec stays byte-untouched; the pin lives in tests/Test_PointSpec.fs,
/// the only place the two modules meet.
/// WHY THERE IS A CHARACTER TABLE HERE (and MLPermSpec.fs has none): Sn is
/// an INDEX-ACTION member, orbit combinatorics on `Idx<N>` powers, never
/// needing an irrep. Point groups are the opposite: a direct sum of
/// labelled irreducible blocks with Hom read off the labels by Schur -- so
/// this file carries baked matrices where MLPermSpec.fs carries partitions.
/// THE FROBENIUS-SCHUR CORRECTION -- the one thing O(3) let us forget. Over
/// R, Schur's lemma gives one free element of the DIVISION ALGEBRA
/// D_i = End_G(U_i), which is R, C or H. Writing e_i = dim_R D_i in
/// {1, 2, 4}, the count is the FS formula, stated once at `genericHomDim`.
/// Every O(3) irrep is of real type (e = 1), which is why MLSpec.homDim can
/// be `sum m_i*n_i` with no correction and still be right. The moment a
/// second block-spec family arrives the factor becomes visible: C4's E
/// label is of COMPLEX type (e = 2) while D4's E -- same dimension, same
/// R90 generator -- is of REAL type (e = 1); see `pgHomDim` for the
/// concrete contrast. The emitted basis of a cell is [Id] at e = 1 and
/// [Id, J] at e = 2, with J a BAKED per-label matrix: basis-relative data
/// with no call site at which it could be derived, so it lives in the table.
/// THE TABLES ARE DATA, AND THE INTEGRITY CERTIFICATE IS THE PROOF
/// OBLIGATION: `certifyPointGroup` runs on every registry fetch (memoized;
/// |G| <= 8 so the cost is microseconds) and failwithf's on any violation --
/// compiler bugs, not user errors. Six integer-exact families:
///
///   1. SHAPE. Distinct label names; every generator and J square of side
///      DimR; matching generator counts across labels.
///   2. CLOSURE. BFS from the identity, elements identified by their TUPLE
///      of matrices across ALL labels (faithful, since a finite group's
///      irreps separate its elements); count must equal |G| and be closed
///      under multiplication.
///   3. ORTHOGONALITY. M^T*M = Id for every element matrix (true here by
///      construction -- C4/D4 were picked for MATRIX RATIONALITY, every
///      entry in {0, +-1}); needed because the Reynolds form
///      `M -> rho_W(g)*M*rho_V(g)^T` is correct only when rho(g)^T = rho(g)^-1.
///   4. THE FS INDICATOR. nu(U) = sum_g chi_U(g^2)/|G|, integer-divisible
///      by |G| (asserted). See `fsIndicator`: a REAL-character convention,
///      reading 1 / 0 / -2, not the textbook 1 / 0 / -1.
///   5. THE J IDENTITIES. J^2 = -Id and commutes with every generator;
///      declared Fs and J must agree (complex needs J, real forbids it).
///      D4's E having no valid J is DESIGN DATA: real type means End is
///      one-dimensional, and no J with J^2 = -Id exists in R.
///   6. THE R-BURNSIDE TRAP. sum_i d_i^2/e_i = |G| (Wedderburn). Catches a
///      MISSING label, wrong dimension, or wrong FS type in one integer:
///      C4 gives 1+1+4/2 = 4, D4 gives 1+1+1+1+4 = 8.
/// THE ROSTER BOUNDARY IS RATIONALITY, NOT CRYSTALLOGRAPHY: {C4, D4} is the
/// pair whose every generator entry lies in {0, +-1}, giving an
/// exact-rational oracle (no field extension, exact float equality for
/// equivariance pins). The Q(sqrt 3) families (trigonal/hexagonal/cubic-E)
/// need a coefficient ring before a table.
/// FsQuat is likewise a RESERVED VALUE, not a dead field: FS in {R, C} for
/// all single point groups, H first at double groups. Counting is uniform
/// in e (`endDim FsQuat = 4`, read like any other) while every
/// emission-adjacent path (`endBasis`) raises a loud internal error; a
/// synthetic label in the tests exercises the count arm, but nothing
/// shipped reaches emission.
module Blade.ML.PointSpec

open System.Collections.Generic

/// The Frobenius-Schur type of an R-irreducible label: which division algebra
/// its endomorphism ring is. `FsQuat` is reserved (see the header) -- legal
/// in every counting path, an internal error in every emitting one.
type FsType =
    | FsReal
    | FsComplex
    | FsQuat

/// e = dim_R End_G(U) in {1, 2, 4} -- the FS weight of a label, and the ONLY
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

// Integer matrices. Rectangular is never needed here -- every matrix in this
// file is a square rep matrix -- so the helpers assume and check squareness.

let matId (n: int) : int[][] =
    Array.init n (fun i -> Array.init n (fun j -> if i = j then 1 else 0))

let matIsSquare (n: int) (m: int[][]) : bool =
    m.Length = n && m |> Array.forall (fun r -> r.Length = n)

let matMul (a: int[][]) (b: int[][]) : int[][] =
    let n = a.Length
    if b.Length <> n then
        failwith $"internal: MLPointSpec.matMul on mismatched sizes ({n} vs {b.Length})"
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

/// A stable textual key for a tuple of matrices -- the element identity used
/// by the closure BFS. A string rather than structural array equality,
/// because `int[][] list` does not hash structurally in .NET.
let private tupleKey (ms: int[][] list) : string =
    ms
    |> List.map (fun m ->
        m |> Array.map (fun r -> r |> Array.map string |> String.concat ",") |> String.concat ";")
    |> String.concat "|"

/// One R-irreducible label. `Gens` is parallel to the group's `GenNames`;
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
/// `Irreps`. One word, all labels -- the common word set of the header.
type PgElement = {
    Word: int list
    Mats: int[][] list
}

// THE CANONICAL TABLES -- frozen data. Every entry is in {0, +-1} (the
// rationality boundary of the header).

/// R90 = [[0, -1], [1, 0]] -- the 90 degree rotation, the E-block generator
/// of both shipped groups, and (at C4) also the baked J.
let private r90 : int[][] = [| [| 0; -1 |]; [| 1; 0 |] |]

/// diag(1, -1) -- the mirror, D4's second E-block generator.
let private mirror : int[][] = [| [| 1; 0 |]; [| 0; -1 |] |]

let private one1 (v: int) : int[][] = [| [| v |] |]

/// C4 = <r | r^4>, order 4. Three R-irreducible labels: the two characters
/// A (r -> 1) and B (r -> -1), and the 2-dimensional E, the real form of
/// the conjugate pair {i, -i}, which is why E is of COMPLEX type and
/// carries J = R90. R-Burnside: 1 + 1 + 4/2 = 4.
let private c4 : PointGroup = {
    Name = "C4"
    Order = 4
    GenNames = [ "r" ]
    Irreps =
        [ { Name = "A"; DimR = 1; Fs = FsReal; Gens = [ one1 1 ]; J = None }
          { Name = "B"; DimR = 1; Fs = FsReal; Gens = [ one1 -1 ]; J = None }
          { Name = "E"; DimR = 2; Fs = FsComplex; Gens = [ r90 ]; J = Some r90 } ]
}

/// D4 = <r, s | r^4, s^2, (sr)^2>, order 8. Four characters and one
/// 2-dimensional E, with the SAME R90 rotation generator as C4's E but of
/// REAL type: the added mirror kills the complex structure (nothing
/// commutes with both R90 and diag(1, -1) except scalars). R-Burnside:
/// 1 + 1 + 1 + 1 + 4 = 8.
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
let pointGroupNames : string list = rawRegistry |> List.map _.Name

/// A hard cap on the BFS, so a malformed table (say a generator of infinite
/// order) fails loudly instead of hanging. No shipped group is near it.
let private closureCap = 256

/// Enumerate the group by BFS over the generators (the common word set): an
/// element is identified by its TUPLE of matrices across all labels, words
/// are shortest, and the enumeration order is stable; `certifyPointGroup`
/// pins the resulting count to the declared order.
let groupElements (grp: PointGroup) : PgElement list =
    let nGen = List.length grp.GenNames
    grp.Irreps
    |> List.iter (fun ir ->
        if List.length ir.Gens <> nGen then
            failwithf "internal: point group %s declares %d generator(s) %A but label %s carries %d generator matrices -- the common word set is not well defined"
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
                    failwith $"internal: the generator closure of point group {grp.Name} exceeded {closureCap} elements (declared order {grp.Order}) -- a generator matrix is not of finite order"
                let el = { Word = cur.Word @ [ gi ]; Mats = nxt }
                out.Add el
                queue.Enqueue el
    List.ofSeq out

/// A word rendered with the group's generator names ("e" for the identity).
let wordName (grp: PointGroup) (w: int list) : string =
    if List.isEmpty w then "e"
    else w |> List.map (fun gi -> List.item gi grp.GenNames) |> String.concat ""

/// The Frobenius-Schur indicator of a label, computed from the enumerated
/// elements: nu(U) = sum_g chi_U(g^2) / |G|, an exact integer (the division
/// is asserted exact by `certifyPointGroup`).
/// THE VALUE CONVENTION, because it is a genuine trap. chi_U here is the
/// character of the R-IRREDUCIBLE label, not of a C-irreducible
/// constituent, and the two conventions disagree at quaternionic type:
///
///   e = 1 (R): U (x) C is C-irreducible with indicator +1        => nu = +1
///   e = 2 (C): U (x) C = W (+) Wbar, indicators 0 + 0             => nu =  0
///   e = 4 (H): U (x) C = W (+) W, indicators (-1) + (-1)          => nu = -2
///
/// i.e. nu = 2 - e exactly, the identity `certifyPointGroup` asserts. The
/// textbook 1 / 0 / -1 triple is the C-irreducible reading; on everything
/// this registry ships the two agree, and the H row is unreachable in a
/// single point group anyway. Stating it as 2 - e keeps the assert exact.
let fsIndicator (grp: PointGroup) (els: PgElement list) (irrepIndex: int) : int =
    let total =
        els |> List.sumBy (fun el ->
            let m = List.item irrepIndex el.Mats
            matTrace (matMul m m))
    if total % grp.Order <> 0 then
        failwith $"internal: the FS indicator of {grp.Name}::{(List.item irrepIndex grp.Irreps).Name} is {total}/{grp.Order}, not an integer -- the element enumeration is wrong"
    total / grp.Order

/// sum_i d_i^2/e_i -- the R-Burnside sum, which must equal |G|.
let burnsideSum (grp: PointGroup) : int =
    grp.Irreps
    |> List.sumBy (fun ir ->
        let e = endDim ir.Fs
        if (ir.DimR * ir.DimR) % e <> 0 then
            failwith $"internal: label {grp.Name}::{ir.Name} has d = {ir.DimR} and e = {e}, but e does not divide d^2 -- no Wedderburn block has that shape"
        (ir.DimR * ir.DimR) / e)

/// What `certifyPointGroup` computed on the way to passing. Returned rather
/// than discarded so the test block can PRINT the numbers (a certificate
/// you cannot read is a certificate you cannot review).
type PgIntegrity = {
    Group: string
    Order: int
    Closure: int
    /// (label, nu) per label, in table order.
    FsIndicators: (string * int) list
    /// sum d_i^2/e_i.
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
        failwith $"internal: point group {grp.Name} declares order {grp.Order}"
    if List.isEmpty grp.Irreps then
        failwith $"internal: point group {grp.Name} has no labels"
    let names = grp.Irreps |> List.map _.Name
    if List.length (List.distinct names) <> List.length names then
        failwithf "internal: point group %s has duplicate label names %A" grp.Name names
    grp.Irreps
    |> List.iter (fun ir ->
        if ir.DimR < 1 then
            failwith $"internal: label {grp.Name}::{ir.Name} has dimension {ir.DimR}"
        ir.Gens
        |> List.iteri (fun gi m ->
            if not (matIsSquare ir.DimR m) then
                failwith $"internal: generator {(List.item gi grp.GenNames)} of {grp.Name}::{ir.Name} is not {ir.DimR} x {ir.DimR}")
        match ir.J with
        | Some j when not (matIsSquare ir.DimR j) ->
            failwith $"internal: J of {grp.Name}::{ir.Name} is not {ir.DimR} x {ir.DimR}"
        | _ -> ())

    // (2) CLOSURE -- count and multiplicative closure over the common word set.
    let els = groupElements grp
    let closure = List.length els
    if closure <> grp.Order then
        failwith $"internal: the generator closure of point group {grp.Name} has {closure} elements but the table declares order {grp.Order} -- the generator matrices do not generate the declared group"
    let keys = HashSet<string>(els |> List.map (fun el -> tupleKey el.Mats))
    for a in els do
        for b in els do
            let prod = List.map2 matMul a.Mats b.Mats
            if not (keys.Contains(tupleKey prod)) then
                failwith $"internal: the enumerated elements of {grp.Name} are not closed under multiplication ({(wordName grp a.Word)} * {(wordName grp b.Word)} escapes)"

    // (3) ORTHOGONALITY -- what makes the oracle's rho(g)^T = rho(g)^-1 legitimate.
    for el in els do
        List.iter2 (fun (m: int[][]) (ir: PgIrrep) ->
            if not (matEq (matMul (matTranspose m) m) (matId ir.DimR)) then
                failwith $"internal: the matrix of {grp.Name}::{ir.Name} at element {(wordName grp el.Word)} is not orthogonal -- the group-average form rho_W(g) M rho_V(g)^T is not the Reynolds operator for this table") el.Mats grp.Irreps

    // (4) THE FS INDICATOR -- nu = 2 - e, exactly (see `fsIndicator`).
    let indicators =
        grp.Irreps
        |> List.mapi (fun i ir ->
            let nu = fsIndicator grp els i
            let want = 2 - endDim ir.Fs
            if nu <> want then
                failwith $"internal: label {grp.Name}::{ir.Name} declares Fs = {(fsName ir.Fs)} (e = {(endDim ir.Fs)}), so the FS indicator sum_g chi(g^2)/|G| must be {want}, but the element enumeration gives {nu}"
            (ir.Name, nu))

    // (5) THE J IDENTITIES, and the declared-Fs/declared-J agreement.
    let jLabels =
        grp.Irreps
        |> List.choose (fun ir ->
            match ir.Fs, ir.J with
            | FsComplex, None ->
                failwith $"internal: label {grp.Name}::{ir.Name} is of complex type (e = 2) but carries no baked J -- the emitted End-basis [Id, J] has nothing to emit"
            | FsReal, Some _ ->
                failwith $"internal: label {grp.Name}::{ir.Name} is of real type (e = 1) but carries a baked J -- End is one-dimensional, so a J would be a scalar with J^2 = -Id"
            | FsQuat, _ ->
                failwith $"internal: label {grp.Name}::{ir.Name} declares FsQuat -- the value is RESERVED for double groups (retired transforms-as-types plan section 3.6); no single point group has a quaternionic label"
            | _, None -> None
            | _, Some j ->
                if not (matEq (matMul j j) (matNeg (matId ir.DimR))) then
                    failwith $"internal: J^2 <> -Id for {grp.Name}::{ir.Name}"
                ir.Gens
                |> List.iteri (fun gi g ->
                    if not (matEq (matMul j g) (matMul g j)) then
                        failwith $"internal: J does not commute with generator {(List.item gi grp.GenNames)} of {grp.Name}::{ir.Name} -- J is not an endomorphism of the label")
                Some ir.Name)

    // (6) THE R-BURNSIDE TRAP.
    let bs = burnsideSum grp
    if bs <> grp.Order then
        failwith $"internal: the R-Burnside sum of {grp.Name} is sum_i d_i^2/e_i = {bs}, but |G| = {grp.Order} -- the label list is incomplete, or a dimension or an FS type is wrong"

    { Group = grp.Name
      Order = grp.Order
      Closure = closure
      FsIndicators = indicators
      Burnside = bs
      JLabels = jLabels
      Words = els |> List.map (fun el -> wordName grp el.Word) }

let private certified = Dictionary<string, PointGroup>()

/// Fetch a registered point group BY NAME, running the integrity
/// certificate the first time (memoized: the tables are immutable, so once
/// is enough, and a violation is a hard failure rather than a cached one).
let pointGroup (name: string) : PointGroup =
    match certified.TryGetValue name with
    | true, g -> g
    | _ ->
        match rawRegistry |> List.tryFind (fun g -> g.Name = name) with
        | None ->
            failwith $"""internal: unknown point group '{name}' -- the registry is {{{(String.concat ", " pointGroupNames)}}}"""
        | Some g ->
            certifyPointGroup g |> ignore
            certified.[name] <- g
            g

/// Look a label up in a group. This is the unknown-label failure point of
/// the counting layer; the SOURCE-level decoder diagnostic
/// (`pgSpecOfStatic`) does not route through here.
let pgIrrep (grp: PointGroup) (label: string) : PgIrrep =
    match grp.Irreps |> List.tryFind (fun ir -> ir.Name = label) with
    | Some ir -> ir
    | None ->
        failwithf "internal: point group %s has no label '%s' -- its labels are {%s}"
            grp.Name label (grp.Irreps |> List.map _.Name |> String.concat ", ")

/// The index of a label in the group's table order -- the index into a
/// `PgElement.Mats` tuple.
let pgIrrepIndex (grp: PointGroup) (label: string) : int =
    match grp.Irreps |> List.tryFindIndex (fun ir -> ir.Name = label) with
    | Some i -> i
    | None ->
        failwithf "internal: point group %s has no label '%s' -- its labels are {%s}"
            grp.Name label (grp.Irreps |> List.map _.Name |> String.concat ", ")

/// The EMITTED End-basis of a cell: [Id] at real type, [Id, J] at complex
/// type. Its length is `endDim ir.Fs` by construction -- the bridge between
/// the count and the emission. This is the emission-adjacent path the
/// header reserves FsQuat against: the counting core reads
/// `endDim FsQuat = 4` happily, but this function refuses, so a
/// quaternionic label can be counted but never emitted.
let endBasis (ir: PgIrrep) : int[][] list =
    match ir.Fs with
    | FsReal -> [ matId ir.DimR ]
    | FsComplex ->
        match ir.J with
        | Some j -> [ matId ir.DimR; j ]
        | None ->
            failwith $"internal: label {ir.Name} is of complex type but has no baked J"
    | FsQuat ->
        failwith $"internal: label {ir.Name} is of quaternionic type -- FsQuat is a RESERVED counting value (retired transforms-as-types plan section 3.6: counts are uniform in e, emission is not). The [1, i, j, k] End-basis of a double-group label has no baked table here and no emitter asking for one"

// THE GENERIC e-WEIGHTED COUNTING CORE. Parameterized over the LABEL type
// alone: a block algebra says how big a label is and how big its
// endomorphism ring is, and everything below is integer arithmetic over
// `('K * int) list` specs. Instantiated at 'K = string (point-group labels,
// below) and, in the test block only, at 'K = (l, parity) with EndDim = 1,
// where it must agree with MLSpec.homDim/homBlocks on a spec sweep -- the
// "twin-not-reroute" pin demonstrating the abstraction against O(3) without
// touching it.

/// How a family of labels sizes: the R-dimension of a label and the
/// R-dimension of its endomorphism ring.
type BlockAlgebra<'K when 'K : comparison> = {
    Dim: 'K -> int
    EndDim: 'K -> int
}

/// sum_i m_i*dim(U_i) -- the R-dimension of the module a spec describes.
let genericTotalDim (alg: BlockAlgebra<'K>) (spec: ('K * int) list) : int =
    spec |> List.sumBy (fun (k, m) -> m * alg.Dim k)

/// Total multiplicity per label. Duplicate spec entries AGGREGATE, matching
/// MLSpec.aggregateByIrrep: a spec is an ordered list of blocks, but the
/// Hom space only sees how many copies of each label there are in total.
let genericAggregate (spec: ('K * int) list) : Map<'K, int> =
    (Map.empty, spec) ||> List.fold (fun acc (k, m) ->
        acc |> Map.change k (fun cur -> Some (defaultArg cur 0 + m)))

/// THE FS FORMULA -- stated once, here, and referenced everywhere else. Over
/// R-irreducible labels U_i with e_i = dim_R End_G(U_i) in {1 (R), 2 (C),
/// 4 (H)}:
///
///     dim_R Hom_G(+_i m_i*U_i, +_i n_i*U_i) = sum_i m_i*n_i*e_i
///
/// Each multiplicity cell carries e_i scalars, not one: Schur over R gives
/// an element of the division algebra End_G(U_i), and the emitted basis of
/// a cell is [Id] at e = 1 and [Id, J] at e = 2 (`endBasis`). At e_i = 1 --
/// every O(3) label, and every label of D4 -- the formula degenerates to
/// the familiar sum m_i*n_i of MLSpec.homDim, so the correction is
/// invisible until a second family arrives.
let genericHomDim (alg: BlockAlgebra<'K>) (specIn: ('K * int) list) (specOut: ('K * int) list) : int =
    let aIn = genericAggregate specIn
    genericAggregate specOut
    |> Map.fold (fun acc k mOut ->
        match Map.tryFind k aIn with
        | Some mIn -> acc + mIn * mOut * alg.EndDim k
        | None -> acc) 0

/// The Hom basis as BLOCK PAIRS: every (input block, output block) pair
/// sharing a label, output-major, mirroring MLSpec.homBlocks (all pairs,
/// not first-match). Each pair contributes mOut*mIn CELLS of e scalars
/// each, so `sum_pairs mOut*mIn*e = genericHomDim` -- the identity the
/// test block pins. Yields (inputBlockIndex, outputBlockIndex,
/// outputEntry, inputEntry), the MLSpec.homBlocks tuple order.
let genericHomBlocks (alg: BlockAlgebra<'K>) (specIn: ('K * int) list) (specOut: ('K * int) list)
                     : (int * int * ('K * int) * ('K * int)) list =
    ignore alg
    let sIn = List.toArray specIn
    let sOut = List.toArray specOut
    [ for bo in 0 .. sOut.Length - 1 do
        for bi in 0 .. sIn.Length - 1 do
          if fst sIn.[bi] = fst sOut.[bo] then
            yield (bi, bo, sOut.[bo], sIn.[bi]) ]

/// A point-group spec: (LABEL_NAME, multiplicity) entries against a group,
/// in block order. Names rather than indices because `SVString` is already
/// first-class in StaticEval, so the name surface costs no new static
/// machinery, and the frozen table names are the diagnostic identity.
type PgSpec = (string * int) list

/// The block algebra of a point group: label -> (DimR, e). Every `pg*`
/// function below is the generic core at this instantiation and nothing else.
let pgAlgebra (grp: PointGroup) : BlockAlgebra<string> =
    { Dim = fun label -> (pgIrrep grp label).DimR
      EndDim = fun label -> endDim (pgIrrep grp label).Fs }

let private checkSpec (grp: PointGroup) (spec: PgSpec) : unit =
    spec |> List.iter (fun (label, m) ->
        pgIrrep grp label |> ignore
        if m < 0 then
            failwith $"internal: spec entry {grp.Name}::{label} has negative multiplicity {m}")

/// dim_R of the module a pg spec describes.
let pgTotalDim (grp: PointGroup) (spec: PgSpec) : int =
    checkSpec grp spec
    genericTotalDim (pgAlgebra grp) spec

/// dim_R Hom_G(V_in, V_out) for a point group -- the FS formula at
/// `genericHomDim`, with e read from the frozen table. THE CONTRAST, in one
/// line: on the spec shape [trivial x 1, E x 2] -> itself this is
/// 1 + 2*2*2 = 9 at C4 and 1 + 2*2*1 = 5 at D4 -- same dimensions, same R90
/// generator, same multiplicities; the FS type of E is the only difference.
let pgHomDim (grp: PointGroup) (specIn: PgSpec) (specOut: PgSpec) : int =
    checkSpec grp specIn
    checkSpec grp specOut
    genericHomDim (pgAlgebra grp) specIn specOut

/// The pg Hom basis as block pairs -- `genericHomBlocks` at the pg
/// instantiation. Each pair (bi, bo, (label, mOut), (label, mIn)) carries
/// mOut*mIn cells of `endBasis` columns apiece.
let pgHomBlocks (grp: PointGroup) (specIn: PgSpec) (specOut: PgSpec)
                : (int * int * (string * int) * (string * int)) list =
    checkSpec grp specIn
    checkSpec grp specOut
    genericHomBlocks (pgAlgebra grp) specIn specOut

/// Block start offsets in the R-module of a spec; length = spec length + 1,
/// last = pgTotalDim. The emitted-basis layout, and what the oracle uses to
/// place a cell's End-basis matrix inside Hom(V, W).
let pgBlockStarts (grp: PointGroup) (spec: PgSpec) : int list =
    checkSpec grp spec
    let alg = pgAlgebra grp
    (0, spec) ||> List.scan (fun acc (k, m) -> acc + m * alg.Dim k)

/// rho_spec(g) -- the BLOCK-DIAGONAL matrix by which an element acts on the
/// R-module a spec describes: block b holds its `mult` copies
/// CONSECUTIVELY, each a full DimR-sized copy of the label's matrix at that
/// element (copy-major, which corpus 045 pins from the value side: at
/// [A1 x 1, E x 2] the rotation sends [x0, x1, x2, x3, x4] to
/// [x0, -x2, x1, -x4, x3]). Integer in, integer out -- every shipped
/// generator entry lies in {0, +-1}, so no coefficient ring is needed.
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

// RESTRICTION FROM O(3) -- THE BRANCHING RULES, shipped as a TABLE.
// WHY AN EMBEDDING IS NEW DATA, NOT DERIVABLE FROM ANYTHING ABOVE. A label
// is a tuple of matrices in its own basis; nothing in the registry says
// which orthogonal transformation of R^3 the generator `r` IS. Restriction
// depends on HOW G sits inside O(3): the same abstract D4 restricts
// differently as the proper rotation group 422 (second generator a
// two-fold AXIS) vs C4v (second generator a MIRROR) -- they differ by the
// inversion, which O(3) irreps see via parity. So the embedding is
// DECLARED here and certified as a faithful homomorphism.
// THE SHIPPED EMBEDDINGS ARE PROPER, WHICH DECIDES PARITY:
//   C4 -> <R_z(90deg)> in SO(3)                (four-fold axis is z)
//   D4 -> <R_z(90deg), R_x(180deg)> in SO(3)   (the crystallographic 422)
// Both land in SO(3) (-I not in G), so the PARITY LABEL NEVER ENTERS THE
// CHARACTER COMPUTATION: D^(l,even)|_G and D^(l,odd)|_G are the same
// module -- a STRENGTH LOSS, since an (l=0, ODD) pseudoscalar restricts to
// the TRIVIAL label, a genuine G-invariant (no element of G is improper).
// `o3Character`'s improper branch is written and, on this roster,
// unreachable -- it fires once an improper embedding (C4v, D4h) ships.
// THE FROBENIUS-SCHUR CORRECTION, AGAIN: over R the multiplicity is NOT
// the character inner product,
//     <chi_U,chi_U> = dim_R Hom_G(U,U) = dim_R End_G(U) = e_U, and
//     <chi_V,chi_U> = m_U * e_U  =>  m_U = <chi_V,chi_U> / e_U,
// so the same e that corrects `genericHomDim` corrects the branching.
// Dropping it: at C4 (E complex, e = 2) the count doubles E, and l = 1
// "decomposes" into 5 dimensions of a 3-dimensional space -- the
// dimension-closure assert below catches this.
// EXACT INTEGER ARITHMETIC throughout -- no angles, no floats. `chiRot`
// runs the Clebsch-Gordan recurrence chi_1*chi_l = chi_(l-1)+chi_l+chi_(l+1)
// on the integer t = tr(R), never asking what the angle is.
// WHAT THIS IS NOT: a reinterpretation of a buffer. `restrictSpec` names
// the DECOMPOSITION; it does NOT say an `IrrepsIdx<S>` buffer IS a
// `PgIrrepsIdx<g, restrictSpec S>` buffer -- the LAYOUTS disagree. O(3)
// orders components by m = -l..l (G-invariant m = 0 in the MIDDLE); a pg
// spec lays each label's copies out CONSECUTIVELY. Already at l = 1 under
// C4 the invariant sits at index 1 on the O(3) side, index 0 on the pg
// side, and no ORDERING fixes it -- the E pair is SPLIT by the A component
// while pg blocks are contiguous. The change of basis is a genuine
// permutation (with signs); a value-level `ml.restrict` would emit it.

/// The geometric embedding of a registered point group into O(3): what each
/// generator IS, as a 3x3 orthogonal integer matrix. `GenMats` is PARALLEL
/// to the group's `GenNames`, exactly as every label's `Gens` is.
type PgEmbedding = {
    Group: string
    GenMats: int[][] list
    /// What each generator is, in words. Diagnostics and review only -- the
    /// reviewable half of the convention, so it is not optional.
    GenWhat: string list
}

/// R_z(90deg) -- the four-fold axis of both shipped groups, in the standard
/// (x, y, z) Cartesian basis.
let private rotZ90 : int[][] = [| [| 0; -1; 0 |]; [| 1; 0; 0 |]; [| 0; 0; 1 |] |]

/// R_x(180deg) = diag(1, -1, -1) -- a two-fold AXIS along x, not a mirror.
/// This single choice makes the shipped D4 the proper group 422 rather
/// than C4v, and fixes the B1/B2 label assignment (along a face diagonal
/// the two swap); the branching multiset is the same either way.
let private rotX180 : int[][] = [| [| 1; 0; 0 |]; [| 0; -1; 0 |]; [| 0; 0; -1 |] |]

let private embeddings : PgEmbedding list =
    [ { Group = "C4"; GenMats = [ rotZ90 ]; GenWhat = [ "R_z(90 deg)" ] }
      { Group = "D4"; GenMats = [ rotZ90; rotX180 ]; GenWhat = [ "R_z(90 deg)"; "R_x(180 deg)" ] } ]

/// The embedding of a registered group. Unregistered = compiler bug: a
/// group in the registry with no embedding has an undefined restriction,
/// and the two lists are meant to stay the same length.
let pgEmbedding (name: string) : PgEmbedding =
    match embeddings |> List.tryFind (fun e -> e.Group = name) with
    | Some e -> e
    | None ->
        failwith $"""internal: point group '{name}' has no declared O(3) embedding -- every registered group needs one before its restriction is defined (the registry is {{{(String.concat ", " pointGroupNames)}}})"""

/// det of a 3x3 integer matrix.
let private det3 (m: int[][]) : int =
    m.[0].[0] * (m.[1].[1] * m.[2].[2] - m.[1].[2] * m.[2].[1])
    - m.[0].[1] * (m.[1].[0] * m.[2].[2] - m.[1].[2] * m.[2].[0])
    + m.[0].[2] * (m.[1].[0] * m.[2].[1] - m.[1].[1] * m.[2].[0])

let private geoCache = Dictionary<string, (PgElement * int[][]) list>()

/// The group's elements paired with the 3x3 matrix each one IS in R^3 --
/// the bridge between the abstract table and O(3), CERTIFIED on first use:
/// (1) every generator matrix is 3x3 and orthogonal (M^T*M = Id); (2) the
/// word map is MULTIPLICATIVE, so "evaluate the word in the geometric
/// generators" is a well-defined homomorphism, not a property of the
/// particular word the BFS handed out; (3) the map is FAITHFUL,
/// |image| = |G| -- non-faithful would embed a QUOTIENT, decomposing the
/// wrong group under the right group's labels.
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
                failwith $"internal: the O(3) embedding of generator {(List.item gi grp.GenNames)} of {grp.Name} is not 3 x 3"
            if not (matEq (matMul (matTranspose m) m) (matId 3)) then
                failwith $"internal: the O(3) embedding of generator {(List.item gi grp.GenNames)} of {grp.Name} is not orthogonal -- it is not an element of O(3)")
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
                        failwith $"internal: the O(3) embedding of {grp.Name} is not a homomorphism -- the geometric matrix of {(wordName grp a.Word)} * {(wordName grp b.Word)} is not the product of their geometric matrices"
                | _ ->
                    failwith $"internal: the enumerated elements of {grp.Name} are not closed under multiplication at the embedding step ({(wordName grp a.Word)} * {(wordName grp b.Word)})"
        // (3) FAITHFULNESS
        let images = pairs |> List.map (fun (_, g) -> tupleKey [ g ]) |> List.distinct
        if List.length images <> grp.Order then
            failwith $"internal: the O(3) embedding of {grp.Name} has image of size {(List.length images)} but |G| = {grp.Order} -- it embeds a QUOTIENT, and restricting along it would decompose the wrong group"
        geoCache.[grp.Name] <- pairs
        pairs

/// chi_l of a PROPER rotation whose 3x3 trace is t, by the Clebsch-Gordan
/// recurrence chi_1*chi_l = chi_(l-1) + chi_l + chi_(l+1), i.e.
/// chi_(l+1) = (t-1)*chi_l - chi_(l-1) with chi_0 = 1, chi_1 = t. Exact in
/// t; no angle is ever named, so there is no float or half-angle to get wrong.
let private chiRot (l: int) (t: int) : int =
    if l < 0 then failwith $"internal: MLPointSpec.chiRot negative l ({l})"
    if l = 0 then 1
    else
        let mutable prev = 1
        let mutable cur = t
        for _ in 2 .. l do
            let nxt = (t - 1) * cur - prev
            prev <- cur
            cur <- nxt
        cur

/// chi of the O(3) irrep (l, parity) at an element of O(3) given as a 3x3
/// orthogonal integer matrix. O(3) = SO(3) x {+-I}, so an improper g
/// factors as (-I)*R with R = -g proper, and rho_(l,p)(-I) = (-1)^p*Id --
/// the whole of the parity handling, and DEAD CODE on the shipped roster.
let o3Character (l: int) (parity: int) (g: int[][]) : int =
    if not (matIsSquare 3 g) then
        failwith "internal: MLPointSpec.o3Character expects a 3 x 3 matrix"
    if parity <> 0 && parity <> 1 then
        failwith $"internal: MLPointSpec.o3Character parity must be 0 (even) or 1 (odd), got {parity}"
    let d = det3 g
    if d <> 1 && d <> -1 then
        failwith $"internal: MLPointSpec.o3Character was handed a matrix of determinant {d} -- not an element of O(3)"
    let chi = chiRot l (matTrace (if d = 1 then g else matNeg g))
    if d = 1 || parity = 0 then chi else -chi

let private restrictCache = Dictionary<string * int * int, PgSpec>()

/// THE BRANCHING RULE for ONE O(3) irrep: D^(l, parity) restricted along
/// the declared embedding, as multiplicities over the group's own labels,
/// in TABLE order with zero multiplicities dropped:
///     m_u = ( (1/|G|)*sum_g chi_(l,p)(g)*chi_u(g) ) / e_u
/// (real characters, so the textbook conjugation is the identity; e_u is
/// the FS correction). THREE integer-exact compiler-bug guards: the inner
/// product must be divisible by |G| and then by e_u (else the embedding,
/// table or character recurrence is wrong); DIMENSION CLOSURE,
/// sum m_u*d_u = 2l+1 (catches a dropped FS correction or missing label);
/// and a CHARACTER CROSS-CHECK at every element -- the trace of
/// `pgElementMatrix` on the result must equal chi_(l,p) there, routed
/// through the emitted LAYOUT so what the pipeline consumes is checked.
let restrictIrrep (grp: PointGroup) (l: int) (parity: int) : PgSpec =
    if l < 0 then failwith $"internal: MLPointSpec.restrictIrrep negative l ({l})"
    if parity <> 0 && parity <> 1 then
        failwith $"internal: MLPointSpec.restrictIrrep parity must be 0 or 1, got {parity}"
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
                    failwith $"internal: the character inner product of D^(l={l}, parity={parity}) restricted to {grp.Name} against label {ir.Name} is {total}/{grp.Order}, not an integer"
                let ip = total / grp.Order
                if ip % e <> 0 then
                    failwith $"internal: <chi of (l={l}, parity={parity}) restricted to {grp.Name}, chi_{ir.Name}> = {ip} is not divisible by e = {e} -- over R the multiplicity is the inner product DIVIDED by dim End, so a non-divisible value means the table's FS type is wrong"
                if ip < 0 then
                    failwith $"internal: D^(l={l}, parity={parity}) restricted to {grp.Name} gives label {ir.Name} NEGATIVE multiplicity {(ip / e)}"
                (ir.Name, ip / e))
            |> List.filter (fun (_, m) -> m > 0)
        let dimSum = spec |> List.sumBy (fun (nm, m) -> m * (pgIrrep grp nm).DimR)
        if dimSum <> 2 * l + 1 then
            failwith $"internal: D^(l={l}, parity={parity}) restricted to {grp.Name} decomposed to {dimSum} dimensions but the irrep has {(2 * l + 1)} -- a dropped Frobenius-Schur correction reads exactly like this"
        for (el, g) in pairs do
            let want = o3Character l parity g
            let got = matTrace (pgElementMatrix grp spec el)
            if got <> want then
                failwith $"internal: the restriction of D^(l={l}, parity={parity}) to {grp.Name} reconstructs character {got} at element {(wordName grp el.Word)}, but the O(3) character there is {want} -- the branching table and the element action disagree"
        restrictCache.[(grp.Name, l, parity)] <- spec
        spec

/// THE BRANCHING RULE for a whole O(3) spec, given as raw (l, parity, mult)
/// triples rather than `MLSpec.Spec`: this module is dependency-free of
/// MLSpec by design, and the caller that has a `Spec` in hand -- MLStatics
/// -- already opens both. Multiplicities AGGREGATE across blocks (the
/// `genericAggregate` rule), and the result is in the group's TABLE order,
/// the same canonicalization `tpSpec` and `powerSpec` use, so the answer is
/// stable to write in an annotation.
let restrictSpec (grp: PointGroup) (entries: (int * int * int) list) : PgSpec =
    let acc =
        (Map.empty, entries)
        ||> List.fold (fun m (l, parity, mult) ->
            if mult < 1 then
                failwith $"internal: MLPointSpec.restrictSpec was handed a block of multiplicity {mult}"
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
        failwith $"internal: restricting a spec of dim {want} to {grp.Name} produced a module of dim {got}"
    out
