/// THE RADICAL-VECTOR LIE DISCHARGER -- the generator-based certification
/// engine's back half. MLPolyExtract normalizes a body to an exact
/// polynomial and discharges the FINITE half (a word set of integer
/// matrices, pure Q); this module is the SECOND CONSUMER of that same normal
/// form -- not a second engine -- and discharges the CONNECTED half: one
/// polynomial identity per Lie-algebra generator of so(3), plus one for the
/// generator of pi_0 (the single element -I, for O(3) but not SO(3)).
///
/// WHY RADICAL VECTORS, AND WHY `mul` IS NOT PART OF THE DISCHARGE
///
/// The certificate's defect, Df(x).(A.x) - A.f(x), is LINEAR in the
/// generator A. Every entry of A in Blade's real basis has the form
/// q*sqrt(n) with q in Q (L_z outright integer -- see the derivation below),
/// and the body's own coefficients are exact rationals, so every defect
/// coefficient is a finite Q-linear combination of square roots of
/// squarefree integers: a RADICAL VECTOR, `Map<squarefree, Rat>`, key 1 the
/// rational part. NO PRODUCT OF TWO IRRATIONAL ENTRIES EVER OCCURS in the
/// discharge -- the only multiplications are scalar*radical (a body
/// coefficient times a generator entry).
///
/// Acceptance is COMPONENTWISE ZERO, needing no independence lemma: a sum of
/// zeros is zero, whatever the sqrt(n) happen to satisfy. Q-independence of
/// {sqrt(n) : n squarefree} is true but NON-LOAD-BEARING here -- it would
/// only matter for COMPLETENESS (a vanishing defect forcing vanishing
/// components), and its failure mode is a sound false-reject.
///
/// `Radical.mul` exists ONLY for the PIN LAYER -- the exact bracket
/// [L_a, L_b] = L_c and Casimir Sum L^2 = -l(l+1)*I checks in
/// tests/Test_LieTables.fs, which multiply generator entries together and
/// need it. Nothing on the discharge path below calls it: the ring IS closed
/// under products, but the discharge never uses that fact, which is why no
/// field extension is required at any l.
///
/// THE GENERATOR TABLES, DERIVED AGAINST THE CODED CONVENTIONS
///
/// Derived against the conventions AS CODED -- `WignerTables.uMatrix` (the
/// real <-> complex change of basis) and `SphericalHarmonics.evalUpTo` (the
/// real solid harmonics the shipped rotation action is FIT from); a
/// convention drift in either would certify a different group action than
/// the compiled program performs, the single unsound failure mode of the
/// stage. tests/Test_LieTables.fs's EXP-PIN closes that loop numerically
/// (exponentiate the tables, compare against the harmonics' own action).
///
/// `Rotations.applyRep` differentiates the Wigner matrix D_l(R) (DEFINED by
/// Y_l(R.v) = D_l(R).Y_l(v)); the generators A_a = d/dtheta D_l(R_a(theta))|0
/// satisfy the so(3) bracket of the 3x3 rotation generators themselves,
/// [A_x, A_y] = A_z and cyclic (pinned in the test block). In the
/// Condon-Shortley complex basis, R_z(theta) sends (x + iy) -> e^{i theta}
/// (x + iy), so A_z = diag(i*mu); differentiating R_y / R_x on the l = 1
/// harmonics and matching the standard ladder normalization gives
///
///     A+[mu][mu+1] = i*a(mu),   A-[mu][mu-1] = i*b(mu),
///     a(mu) = sqrt((l-mu)(l+mu+1)),  b(mu) = sqrt((l+mu)(l-mu+1)) = a(mu-1),
///     A_x = (A+ + A-)/2,     A_y = (A+ - A-)/(2i).
///
/// (b(mu) = a(mu-1): the same radicand family read from both ends, which is
/// why the realified table is antisymmetric entry by entry, not just in
/// aggregate.)
///
/// STEP 3: realification, A = U.A_complex.U-dagger, with U the CODED
/// `uMatrix`:
///
///     Y^r_0    = Y^c_0                              (row c = l, entry 1)
///     Y^r_{+m} = (Y^c_{-m} + eps_m Y^c_{+m})/sqrt(2)       (eps_m = (-1)^m)
///     Y^r_{-m} = i(Y^c_{-m} - eps_m Y^c_{+m})/sqrt(2)
///
/// Write R_m := Y^r_{+m} (stored at index c = l+m) and I_m := Y^r_{-m}
/// (stored at c = l-m). Substituting the ladder action, with I0 := 0 (there
/// is no such component -- the m = 0 row is R0 alone):
///
///     L_z:  dR_m = -m*I_m,          dI_m = +m*R_m          (m >= 1)
///     L_y:  dR0  = -(a(0)/sqrt(2))*R1
///           dR_m = (b(m)/2)*R_{m-1} - (a(m)/2)*R_{m+1}     (m >= 2)
///           dR1  = (b(1)/sqrt(2))*R0    - (a(1)/2)*R2
///           dI_m = (b(m)/2)*I_{m-1} - (a(m)/2)*I_{m+1}     (m >= 1, I0 = 0)
///     L_x:  dR0  = +(a(0)/sqrt(2))*I1
///           dR_m = (b(m)/2)*I_{m-1} + (a(m)/2)*I_{m+1}     (m >= 1, I0 = 0)
///           dI1  = -(b(1)/sqrt(2))*R0    - (a(1)/2)*R2
///           dI_m = -(b(m)/2)*R_{m-1} - (a(m)/2)*R_{m+1}    (m >= 2)
///
/// THREE THINGS THE CODED CONVENTIONS FORCE that a textbook table would get
/// wrong here:
///
///  (i) THE sqrt(2) AT THE m = 0 SEAM. `uMatrix` puts a bare 1 (not
///      1/sqrt(2)) in the m = 0 row, since Y^r_0 IS Y^c_0. Every entry
///      touching m = 0 carries an extra sqrt(2): R0<->R1 and R0<->I1 couple
///      via (1/2)*sqrt(2*l(l+1)) where every other entry is
///      (1/2)*sqrt(integer). This is why the tables special-case m = 1
///      instead of a uniform loop, and why radicands can reach 2*l(l+1)
///      (~4l^2) rather than l(l+1).
/// (ii) THE CONDON-SHORTLEY eps_m = (-1)^m IN `uMatrix` MAKES THE TABLE
///      BLOCK-STRUCTURED. Because eps_m*eps_{m+/-1} = -1, cross terms cancel
///      in exactly one of the two combinations at every step: L_y couples
///      R<->R and I<->I only, L_x couples R<->I only, L_z pairs (R_m, I_m).
///      Drop the phase and every generator becomes dense with the wrong
///      signs. (SymPowerTables' phase rule rests on the same fixed-vector
///      property of `uMatrix`.)
/// (iii) THE SIGN OF L_z IS FIXED BY THE ROW ORDER, NOT BY TASTE. With
///      c = m + l ascending, the L_z 2x2 block on (R_m, I_m) = (c = l+m,
///      c = l-m) reads [[0, -m], [m, 0]]. At l = 1 the real basis is
///      (y, z, x) -- the ml-spec's own order -- and the table reproduces
///      d/dtheta R_z(theta) conjugated by that permutation, entry for entry.
///      A "nicer-looking" sign convention would certify the INVERSE rotation
///      and silently accept the mirror image of every pseudovector body.
///
/// Assembly is block-diagonal per spec, one copy of the (2l+1)x(2l+1) table
/// per multiplicity copy, matching `Rotations.applyRep`'s layout. PARITY
/// PLAYS NO ROLE IN THE so(3) ACTION -- separate integer bookkeeping (the -I
/// identity below), which is why O(3) costs 3 + 1 checks and SO(3) costs
/// 3 + 0.
///
/// THE -I IDENTITY (O(3) only). O(3) = SO(3) x {+-I}: the connected part is
/// discharged by the three generators above, and pi_0 by the single element
/// -I, which acts on an IrrepsIdx block of parity p by the SCALAR (-1)^p
/// (`Rotations.applyRepImproper`'s parity factor at R = identity). Each rep
/// variable carries its block's parity bit, a monomial's sign under -I is
/// (-1)^{Sum parities}, and f(-I.x) = rho(-I).f(x) is a purely INTEGER
/// statement that every monomial of output component c has parity equal to
/// c's own. No radical, no rational, no tolerance: a parity mismatch on a
/// nonzero coefficient is a rejection -- the check that separates
/// u.(v x w) declared (0, odd) -- true -- from the same body declared
/// (0, even) -- false, and true again once the certificate weakens to SO(3).
///
/// THE POST-ACCEPT FLOAT GUARD. After an EXACT accept, the identity is
/// re-evaluated in plain floats at three deterministic sample points per
/// generator, by a different route (term-by-term numeric differentiation of
/// the extracted polynomial, never touching the symbolic substitution or the
/// radical arithmetic). A violation is a COMPILER BUG, not a data condition,
/// and raises `LieGuardFailure` rather than a verdict -- the SymPowerTables
/// gapped-assert discipline; MLEquiv's totality wrapper re-raises it on
/// purpose.
module Blade.ML.LieDischarge

open System.Collections.Generic

module PX = Blade.ML.PolyExtract
module MLS = Blade.ML.Spec

// Radical vectors

/// A Q-linear combination of square roots of SQUAREFREE positive integers,
/// sparse. Key 1 is the rational part. INVARIANT: no zero coefficients, so
/// `Map.isEmpty` is "is zero" and structural equality is value equality.
type Radical = Map<int, PX.Rat>

/// n = s^2*k with k squarefree; returns (s, k). Trial division is ample:
/// radicands are at most 2*l(l+1) ~ 4l^2, a two-digit number at the l <= 8
/// end of any shipped spec.
let squarefree (n: int) : int * int =
    if n <= 0 then failwithf "internal: MLLieDischarge.squarefree of %d" n
    let mutable rest = n
    let mutable s = 1
    let mutable d = 2
    while d * d <= rest do
        while rest % (d * d) = 0 do
            rest <- rest / (d * d)
            s <- s * d
        d <- d + 1
    (s, rest)

[<RequireQualifiedAccess>]
module Radical =
    let zero : Radical = Map.empty

    let isZero (r: Radical) = Map.isEmpty r

    let ofRat (q: PX.Rat) : Radical =
        if PX.Rat.isZero q then Map.empty else Map.ofList [ (1, q) ]

    let ofInt (n: int) : Radical = ofRat (PX.Rat.ofInt n)

    /// q*sqrt(n) as a radical vector (n >= 0; n = 0 is the zero vector). The
    /// ONLY place a square root enters the module.
    let ofSqrt (q: PX.Rat) (n: int) : Radical =
        if PX.Rat.isZero q || n = 0 then Map.empty
        else
            let (s, k) = squarefree n
            let c = PX.Rat.mul q (PX.Rat.ofInt s)
            if PX.Rat.isZero c then Map.empty else Map.ofList [ (k, c) ]

    let private addKey (k: int) (c: PX.Rat) (r: Radical) : Radical =
        if PX.Rat.isZero c then r
        else
            match Map.tryFind k r with
            | None -> Map.add k c r
            | Some old ->
                let s = PX.Rat.add old c
                if PX.Rat.isZero s then Map.remove k r else Map.add k s r

    let add (a: Radical) (b: Radical) : Radical = b |> Map.fold (fun acc k c -> addKey k c acc) a
    let neg (a: Radical) : Radical = a |> Map.map (fun _ c -> PX.Rat.neg c)
    let sub (a: Radical) (b: Radical) : Radical = b |> Map.fold (fun acc k c -> addKey k (PX.Rat.neg c) acc) a

    /// SCALAR*RADICAL -- the only multiplication the discharge performs (a
    /// body coefficient scaling a generator entry). See the module header.
    let scale (q: PX.Rat) (a: Radical) : Radical =
        if PX.Rat.isZero q then Map.empty else a |> Map.map (fun _ c -> PX.Rat.mul q c)

    /// RADICAL*RADICAL -- THE PIN LAYER ONLY. The bracket and Casimir checks
    /// in tests/Test_LieTables.fs multiply generator entries together and
    /// need this; NOTHING on the discharge path calls it. The defect's
    /// linearity in A is what makes that possible -- why no field extension
    /// is needed at any l.
    let mul (a: Radical) (b: Radical) : Radical =
        let mutable out = zero
        for KeyValue (ka, ca) in a do
            for KeyValue (kb, cb) in b do
                let (s, k) = squarefree (ka * kb)
                out <- addKey k (PX.Rat.mul (PX.Rat.mul ca cb) (PX.Rat.ofInt s)) out
        out

    /// The float image -- used ONLY by the near-miss diagnostic and by the
    /// post-accept guard. Never by a verdict.
    let toFloat (r: Radical) : float =
        r |> Map.fold (fun acc k c -> acc + PX.Rat.toFloat c * sqrt (float k)) 0.0

    /// How a radical vector reads in a diagnostic: `0`, `-1`, `3/2*sqrt(2)`,
    /// `1 + -1/2*sqrt(3)`. Deterministic (ascending radicand).
    let render (r: Radical) : string =
        if Map.isEmpty r then "0"
        else
            r
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (k, q) ->
                if k = 1 then PX.Rat.render q else sprintf "%s*sqrt(%d)" (PX.Rat.render q) k)
            |> String.concat " + "

// The exact generator tables

type Axis =
    | Lx
    | Ly
    | Lz

let axisName (a: Axis) : string =
    match a with
    | Lx -> "Lx"
    | Ly -> "Ly"
    | Lz -> "Lz"

/// The three spanning generators, in the order diagnostics report them.
let axes : Axis list = [ Lx; Ly; Lz ]

let private blockCache = Dictionary<Axis * int, Radical [][]>()

/// dpi_l(L_a) in the CODED real basis (index c = m + l), exactly. See the
/// module header for the derivation; shapes are L_z: integers +/-m; L_x,
/// L_y: (1/2)*sqrt(integer), with the m = 0 seam carrying an extra sqrt(2).
let blockGenerator (axis: Axis) (l: int) : Radical [][] =
    if l < 0 then failwithf "internal: MLLieDischarge.blockGenerator negative l (%d)" l
    match blockCache.TryGetValue((axis, l)) with
    | true, v -> v
    | _ ->
        let d = 2 * l + 1
        let m = Array.init d (fun _ -> Array.create d Radical.zero)
        // a(k) = (l-k)(l+k+1), b(k) = (l+k)(l-k+1) = a(k-1) -- INTEGER
        // radicands; the square root is taken once, in `Radical.ofSqrt`.
        let a (k: int) = (l - k) * (l + k + 1)
        let b (k: int) = (l + k) * (l - k + 1)
        let half = PX.Rat.make (bigint 1) (bigint 2)
        let negHalf = PX.Rat.make (bigint -1) (bigint 2)
        match axis with
        | Lz ->
            for k in 1 .. l do
                m.[l + k].[l - k] <- Radical.ofInt (-k)
                m.[l - k].[l + k] <- Radical.ofInt k
        | Ly ->
            if l >= 1 then
                // The m = 0 seam: dR0 = -(a(0)/sqrt(2))*R1, dR1 = +(b(1)/sqrt(2))*R0.
                m.[l].[l + 1] <- Radical.ofSqrt negHalf (2 * a 0)
                m.[l + 1].[l] <- Radical.ofSqrt half (2 * b 1)
            for k in 2 .. l do
                m.[l + k].[l + k - 1] <- Radical.ofSqrt half (b k)
                m.[l - k].[l - k + 1] <- Radical.ofSqrt half (b k)
            for k in 1 .. l - 1 do
                m.[l + k].[l + k + 1] <- Radical.ofSqrt negHalf (a k)
                m.[l - k].[l - k - 1] <- Radical.ofSqrt negHalf (a k)
        | Lx ->
            if l >= 1 then
                // The m = 0 seam: dR0 = +(a(0)/sqrt(2))*I1, dI1 = -(b(1)/sqrt(2))*R0.
                m.[l].[l - 1] <- Radical.ofSqrt half (2 * a 0)
                m.[l - 1].[l] <- Radical.ofSqrt negHalf (2 * b 1)
            for k in 2 .. l do
                m.[l + k].[l - k + 1] <- Radical.ofSqrt half (b k)
                m.[l - k].[l + k - 1] <- Radical.ofSqrt negHalf (b k)
            for k in 1 .. l - 1 do
                m.[l + k].[l - k - 1] <- Radical.ofSqrt half (a k)
                m.[l - k].[l + k + 1] <- Radical.ofSqrt negHalf (a k)
        blockCache.[(axis, l)] <- m
        m

let private specCache = Dictionary<Axis * MLS.Spec, Radical [][]>()

/// dpi_spec(L_a) -- block-diagonal over the spec's blocks, one copy of the
/// block table per multiplicity copy, in `Rotations.applyRep`'s exact layout
/// (block start + copy*(2l+1) + m). Cached per (axis, spec), like
/// `realCGDense` per (l1, l2, l3).
let specGenerator (axis: Axis) (s: MLS.Spec) : Radical [][] =
    match specCache.TryGetValue((axis, s)) with
    | true, v -> v
    | _ ->
        let n = MLS.totalDim s
        let out = Array.init n (fun _ -> Array.create n Radical.zero)
        let starts = MLS.blockStarts s |> List.toArray
        s
        |> List.iteri (fun bi (e: MLS.SpecEntry) ->
            let g = blockGenerator axis e.L
            let d = 2 * e.L + 1
            for copy in 0 .. e.Mult - 1 do
                let st = starts.[bi] + copy * d
                for i in 0 .. d - 1 do
                    for j in 0 .. d - 1 do
                        if not (Radical.isZero g.[i].[j]) then out.[st + i].[st + j] <- g.[i].[j])
        specCache.[(axis, s)] <- out
        out

/// The parity bit (0 even, 1 odd) of every component of an IrrepsIdx<spec>
/// buffer -- the -I bookkeeping's whole input.
let specParity (s: MLS.Spec) : int [] =
    let n = MLS.totalDim s
    let out = Array.zeroCreate n
    let starts = MLS.blockStarts s |> List.toArray
    s
    |> List.iteri (fun bi (e: MLS.SpecEntry) ->
        for i in starts.[bi] .. starts.[bi] + MLS.blockDim e - 1 do
            out.[i] <- e.Parity % 2)
    out

// What the discharge is handed

/// One Lie-algebra generator as it acts on THIS function's data: a printable
/// name, the input matrix for every rep parameter, and the output matrix.
/// Assembling these from specs is the caller's job -- the same separation
/// that makes MLPolyExtract's `ElementAction` group-agnostic.
type LieGenerator = {
    Name: string
    InMats: Map<string, Radical [][]>
    OutMat: Radical [][]
}

/// The pi_0 obligation: one parity bit per component, per rep parameter and
/// for the output. `None` at the call site means SO(3) -- no component
/// group, no check.
type InversionCheck = {
    InPar: Map<string, int []>
    OutPar: int []
}

/// The first coefficient at which a Lie identity fails.
type LieFailure = {
    /// "Lx" | "Ly" | "Lz".
    Generator: string
    Component: int
    Monomial: string
    /// Total degree in the rep components; 0 is the constant obligation.
    RepDegree: int
    /// Df(x).(A.x) coefficient vs (A.f(x)) coefficient, as radical vectors.
    Lhs: Radical
    Rhs: Radical
    /// The mandatory near-miss net, on the residual's FLOAT image (the
    /// truncated-decimal trap). Same rule as MLPolyExtract's finite half.
    NearMiss: bool
}

/// The first monomial at which the -I identity fails. Purely integer.
type InversionFailure = {
    Component: int
    Monomial: string
    /// Sum (exponent * block parity) over the monomial's rep factors,
    /// UNREDUCED -- a reader can check the count against the body by eye.
    ParitySum: int
    /// That sum mod 2: (-1)^{MonoParity} is the monomial's sign under -I.
    MonoParity: int
    /// The declared parity of the output component it sits in.
    OutParity: int
}

type LieError =
    | GeneratorCheck of LieFailure
    | ParityCheck of InversionFailure
    | DischargeCap of string

/// The post-accept float guard's verdict. A COMPILER BUG, never a data
/// condition -- see the module header.
exception LieGuardFailure of string

// The discharge

/// The near-miss rule, identical to MLPolyExtract's: the residual is nonzero
/// but its float image is negligible against the scale of the two sides --
/// the signature of `0.5773502` written where 1/sqrt(3) was meant, never of
/// a wrong sign or a factor of two.
let private nearMissThreshold = 1e-6

let private isNearMiss (lhs: Radical) (rhs: Radical) : bool =
    let residual = abs (Radical.toFloat (Radical.sub lhs rhs))
    let scale = max 1.0 (max (abs (Radical.toFloat lhs)) (abs (Radical.toFloat rhs)))
    residual > 0.0 && residual <= nearMissThreshold * scale

/// Radical-valued sparse polynomials, used ONLY inside the discharge (both
/// sides of one identity, for one output component, at a time).
type private RPoly = Map<PX.Mono, Radical>

let private radAdd (m: PX.Mono) (r: Radical) (p: RPoly) : RPoly =
    if Radical.isZero r then p
    else
        match Map.tryFind m p with
        | None -> Map.add m r p
        | Some old ->
            let s = Radical.add old r
            if Radical.isZero s then Map.remove m p else Map.add m s p

/// Deterministic monomial order, same rule as MLPolyExtract's
/// `Poly.unionMonos`: rep degree ascending, then rendered form -- so "the
/// first offending coefficient" names a property of the polynomials, not a
/// hash walk.
let private unionMonos (a: RPoly) (b: RPoly) : PX.Mono list =
    Set.union (a |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (b |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
    |> Set.toList
    |> List.sortBy (fun m -> (PX.Mono.repDegree m, PX.Mono.render m))

/// Df(x).(A.x) for ONE output component, as a radical-coefficient
/// polynomial. The substitution is A-LINEAR: each rep variable contributes
/// Sum_j A[i][j]*x_j, and the body's rational coefficient multiplies the
/// generator entry as a SCALAR. No radical ever meets another radical.
let private defectLhs (budget: int ref) (gen: LieGenerator) (comp: PX.Poly)
    : Result<RPoly, LieError> =
    let mutable failed : LieError option = None
    let mutable out : RPoly = Map.empty
    for KeyValue (mono, coef) in comp do
        if failed.IsNone then
            for KeyValue (key, e) in mono.Rep do
                if failed.IsNone then
                    let (pname, i) = key
                    match Map.tryFind pname gen.InMats with
                    | None ->
                        // Cannot arise: the caller builds matrices from the same Rep
                        // parameters the extractor made variables of -- an internal
                        // error, not a silent widening (a missing image would verify a WEAKER identity).
                        failed <- Some (DischargeCap (sprintf "internal: no Lie action supplied for '%s'" pname))
                    | Some mat when i < 0 || i >= mat.Length ->
                        failed <- Some (DischargeCap (sprintf "internal: component %d of '%s' is outside its %d-dimensional action" i pname mat.Length))
                    | Some mat ->
                        let baseRep =
                            if e = 1 then Map.remove key mono.Rep else Map.add key (e - 1) mono.Rep
                        let scalar = PX.Rat.mul (PX.Rat.ofInt e) coef
                        for j in 0 .. mat.Length - 1 do
                            if failed.IsNone then
                                let entry = mat.[i].[j]
                                if not (Radical.isZero entry) then
                                    let rep = baseRep |> Map.change (pname, j) (fun c -> Some (defaultArg c 0 + 1))
                                    out <- radAdd { mono with Rep = rep } (Radical.scale scalar entry) out
                                    budget.Value <- budget.Value - 1
                                    if budget.Value < 0 then
                                        failed <- Some (DischargeCap (sprintf "the Lie substitution exceeded the %d-term cap" PX.maxTerms))
    match failed with
    | Some e -> Error e
    | None -> Ok out

/// A.f(x) for ONE output component -- a radical-scaled sum of the OTHER
/// components, where the linearity in A is most visible.
let private defectRhs (gen: LieGenerator) (form: PX.PolyForm) (c: int) : RPoly =
    let mutable out : RPoly = Map.empty
    let row = gen.OutMat.[c]
    for j in 0 .. form.Components.Length - 1 do
        let entry = row.[j]
        if not (Radical.isZero entry) then
            for KeyValue (m, coef) in form.Components.[j] do
                out <- radAdd m (Radical.scale coef entry) out
    out

// The post-accept float guard

/// A deterministic, nonzero, sign-varying sample value. Not random: a
/// compiler must give the same verdict on the same input every time.
let private sampleValue (seed: int) (k: int) : float =
    let h = abs ((seed * 1103515245) ^^^ (k * 12345) ^^^ 1013904223) % 100000
    let v = 0.25 + float (h % 977) / 800.0
    if (h / 977) % 2 = 0 then v else -v

let private monoValue (repVals: Map<string * int, float>) (invVals: Map<PX.InvAtom, float>)
                      (skip: (string * int) option) (m: PX.Mono) : float =
    let mutable acc = 1.0
    for KeyValue (k, e) in m.Rep do
        let e' = match skip with Some s when s = k -> e - 1 | _ -> e
        let v = defaultArg (Map.tryFind k repVals) 0.0
        for _ in 1 .. e' do acc <- acc * v
    for KeyValue (a, e) in m.Inv do
        let v = defaultArg (Map.tryFind a invVals) 0.0
        for _ in 1 .. e do acc <- acc * v
    acc

let private polyValue (repVals: Map<string * int, float>) (invVals: Map<PX.InvAtom, float>) (p: PX.Poly) : float =
    let mutable acc = 0.0
    for KeyValue (m, c) in p do
        acc <- acc + PX.Rat.toFloat c * monoValue repVals invVals None m
    acc

/// Re-check every identity in plain floats by a DIFFERENT route:
/// term-by-term numeric differentiation against a float image of the
/// generator, with no symbolic substitution or radical arithmetic anywhere.
/// Disagreement with the exact accept is a compiler bug.
let private floatGuard (form: PX.PolyForm) (gens: LieGenerator list) (inv: InversionCheck option) : unit =
    // Every rep key and invariant atom the form mentions, in a deterministic
    // order, so the sample point is a function of the polynomial alone.
    let repKeys =
        form.Components
        |> Array.collect (fun p -> p |> Map.toArray |> Array.collect (fun (m, _) -> m.Rep |> Map.toArray |> Array.map fst))
        |> Array.distinct
        |> Array.sort
    let invKeys =
        form.Components
        |> Array.collect (fun p -> p |> Map.toArray |> Array.collect (fun (m, _) -> m.Inv |> Map.toArray |> Array.map fst))
        |> Array.distinct
        |> Array.sortBy (fun a -> (a.Name, defaultArg a.Index -1))
    for sample in 0 .. 2 do
        let repVals = repKeys |> Array.mapi (fun i k -> (k, sampleValue (7 + sample) i)) |> Map.ofArray
        let invVals = invKeys |> Array.mapi (fun i k -> (k, sampleValue (91 + sample) i)) |> Map.ofArray
        for gen in gens do
            // (A.x)_{p,i} in floats.
            let ax =
                gen.InMats
                |> Map.toList
                |> List.collect (fun (pname, mat) ->
                    [ for i in 0 .. mat.Length - 1 ->
                        let mutable acc = 0.0
                        for j in 0 .. mat.Length - 1 do
                            let e = mat.[i].[j]
                            if not (Radical.isZero e) then
                                acc <- acc + Radical.toFloat e * defaultArg (Map.tryFind (pname, j) repVals) 0.0
                        ((pname, i), acc) ])
                |> Map.ofList
            for c in 0 .. form.Components.Length - 1 do
                let mutable lhs = 0.0
                for KeyValue (m, coef) in form.Components.[c] do
                    for KeyValue (key, e) in m.Rep do
                        lhs <- lhs + PX.Rat.toFloat coef * float e
                                     * monoValue repVals invVals (Some key) m
                                     * defaultArg (Map.tryFind key ax) 0.0
                let mutable rhs = 0.0
                for j in 0 .. form.Components.Length - 1 do
                    let e = gen.OutMat.[c].[j]
                    if not (Radical.isZero e) then
                        rhs <- rhs + Radical.toFloat e * polyValue repVals invVals form.Components.[j]
                let scale = max 1.0 (max (abs lhs) (abs rhs))
                if abs (lhs - rhs) > 1e-8 * scale then
                    raise (LieGuardFailure
                        (sprintf "internal: the exact %s discharge accepted but the float shadow disagrees at output component %d (lhs %.17g, rhs %.17g, sample %d) -- this is a compiler bug in the radical-vector Lie discharger, not a property of the checked program"
                            gen.Name c lhs rhs sample))
        match inv with
        | None -> ()
        | Some ip ->
            let flipped =
                repVals |> Map.map (fun (pname, i) v ->
                    match Map.tryFind pname ip.InPar with
                    | Some par when i < par.Length && par.[i] = 1 -> -v
                    | _ -> v)
            for c in 0 .. form.Components.Length - 1 do
                let lhs = polyValue flipped invVals form.Components.[c]
                let sgn = if ip.OutPar.[c] = 1 then -1.0 else 1.0
                let rhs = sgn * polyValue repVals invVals form.Components.[c]
                let scale = max 1.0 (max (abs lhs) (abs rhs))
                if abs (lhs - rhs) > 1e-8 * scale then
                    raise (LieGuardFailure
                        (sprintf "internal: the exact -I discharge accepted but the float shadow disagrees at output component %d (lhs %.17g, rhs %.17g, sample %d) -- this is a compiler bug in the radical-vector Lie discharger, not a property of the checked program"
                            c lhs rhs sample))

/// THE LIE DISCHARGE. For every generator A of so(3), compare Df(x).(A.x)
/// with A.f(x) COEFFICIENTWISE, each coefficient a RADICAL VECTOR checked
/// componentwise-zero; then, under O(3), the integer -I identity. `Ok ()` =
/// the certificate holds -- after which the float guard has also agreed.
///
/// Three generators SPAN so(3) and the defect is linear in A, so three
/// checks are the whole connected obligation -- span-linearity replaces any
/// Lie-closure argument (BladeGenerator.v's proved column).
let discharge (form: PX.PolyForm) (gens: LieGenerator list) (inv: InversionCheck option)
    : Result<unit, LieError> =
    let n = form.Components.Length
    let shapeError =
        gens
        |> List.tryPick (fun g ->
            if g.OutMat.Length <> n then
                Some (DischargeCap (sprintf "internal: the %s output action is %d-dimensional but the body has %d components" g.Name g.OutMat.Length n))
            else None)
    match shapeError with
    | Some e -> Error e
    | None ->
        match inv with
        | Some ip when ip.OutPar.Length <> n ->
            Error (DischargeCap (sprintf "internal: the -I output parity has %d entries but the body has %d components" ip.OutPar.Length n))
        | _ ->
            let budget = ref PX.maxTerms
            let rec loopGens (gs: LieGenerator list) : Result<unit, LieError> =
                match gs with
                | [] -> Ok ()
                | gen :: rest ->
                    let rec comps (c: int) : Result<unit, LieError> =
                        if c >= n then loopGens rest
                        else
                            match defectLhs budget gen form.Components.[c] with
                            | Error e -> Error e
                            | Ok lhs ->
                                let rhs = defectRhs gen form c
                                let offending =
                                    unionMonos lhs rhs
                                    |> List.tryPick (fun m ->
                                        let a = defaultArg (Map.tryFind m lhs) Radical.zero
                                        let b = defaultArg (Map.tryFind m rhs) Radical.zero
                                        if a = b then None else Some (m, a, b))
                                match offending with
                                | None -> comps (c + 1)
                                | Some (m, a, b) ->
                                    Error (GeneratorCheck
                                        { Generator = gen.Name
                                          Component = c
                                          Monomial = PX.Mono.render m
                                          RepDegree = PX.Mono.repDegree m
                                          Lhs = a
                                          Rhs = b
                                          NearMiss = isNearMiss a b })
                    comps 0
            let parityPass () =
                match inv with
                | None -> Ok ()
                | Some ip ->
                    let rec comps (c: int) : Result<unit, LieError> =
                        if c >= n then Ok ()
                        else
                            let offending =
                                form.Components.[c]
                                |> Map.toList
                                |> List.map fst
                                |> List.sortBy (fun m -> (PX.Mono.repDegree m, PX.Mono.render m))
                                |> List.tryPick (fun m ->
                                    let p =
                                        m.Rep
                                        |> Map.fold (fun acc (pname, i) e ->
                                            let bit =
                                                match Map.tryFind pname ip.InPar with
                                                | Some par when i < par.Length -> par.[i]
                                                | _ -> 0
                                            acc + e * bit) 0
                                    let pm = ((p % 2) + 2) % 2
                                    if pm = ip.OutPar.[c] then None else Some (m, p, pm))
                            match offending with
                            | None -> comps (c + 1)
                            | Some (m, p, pm) ->
                                Error (ParityCheck
                                    { Component = c
                                      Monomial = PX.Mono.render m
                                      ParitySum = p
                                      MonoParity = pm
                                      OutParity = ip.OutPar.[c] })
                    comps 0
            match loopGens gens with
            | Error e -> Error e
            | Ok () ->
                match parityPass () with
                | Error e -> Error e
                | Ok () ->
                    floatGuard form gens inv
                    Ok ()
