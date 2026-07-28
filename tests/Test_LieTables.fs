// THE 𝔰𝔬(3) GENERATOR TABLES AND THE RADICAL-VECTOR DISCHARGE — stage 6c of
// plan-transforms-as-types (§3.5's linearity resolution, §7 stage 6c's oracle
// layers). Five layers, keystone first.
//
// 1. THE EXP-PIN (the keystone). The shipped exact tables are DERIVED against
//    `WignerTables.uMatrix`; the shipped certificates are about the rotation
//    action `Rotations.applyRep` performs, which is FIT from
//    `SphericalHarmonics.eval` and never touches uMatrix. If those two
//    conventions ever drifted, this checker would certify a different group
//    action than the compiled program performs — the ONE unsound failure mode
//    of the whole stage. So: float-assemble each table, EXPONENTIATE it, and
//    compare against the Wigner matrix fit from the harmonics themselves,
//    per axis, at several angles, including the house R = Rz(0.7)·Ry(1.1)
//    composition that the ml-equiv/018 certificate pins use.
//
//    The harmonics are re-implemented HERE, from the ml-spec formulas, on
//    purpose: `SphericalHarmonics.fs` lives in the BladeML project which this
//    assembly does not reference, and an independent transcription is a
//    stronger oracle than a shared call would be. D is solved from 2l+1
//    generic samples (Gauss–Jordan) and residual-verified on fresh vectors,
//    which is `Rotations.wignerD`'s own recipe.
//
// 2. EXACT TABLE PINS. Skew-symmetry per radical component; the brackets
//    [L_x, L_y] = L_z and cyclic; the Casimir Σ L² = −l(l+1)·I. All exact,
//    all l ≤ 4, all through `Radical.mul` — which exists FOR THIS LAYER and
//    for nothing else (the discharge never multiplies two radicals; see the
//    module header of MLLieDischarge.fs).
//
// 3. KNOWN ANSWERS, at the discharger's own level. The corpus (ml-equiv
//    063-067) pins these as compiled values and diagnostics; here they are
//    pinned as verdicts, which is what makes the negative controls runnable.
//
// 4. NEGATIVE CONTROLS, live-failing then standing (the 5b-0 discipline): a
//    coefficient perturbed by an exact 1/7, the truncated-1/√3 near miss, and
//    a wrong declared parity that only −I can catch.
//
//    THE LIVE-FAILING RUNS, recorded (2026-07-27, stage 6c). Each was built
//    and run against a deliberately broken discharger, then reverted:
//      * −I check removed → the POST-ACCEPT FLOAT GUARD fired first, as
//        `LieGuardFailure` surfacing through MLEquiv's totality wrapper as an
//        internal-compiler-error BL9001 (which is what the deliberate
//        `reraise ()` hole in that wrapper is for);
//      * −I check AND the guard's inversion arm removed → the three parity
//        controls below fail, together with diagnostics/027 and
//        ml-equiv/064;
//      * L_z's sign flipped (the "looks nicer" mistake the MLLieDischarge
//        header warns about) → the exp-pin fails at ~1.8 and the brackets
//        fail, while SKEW-SYMMETRY AND THE CASIMIR STILL PASS — a global sign
//        on one generator is invisible to both;
//      * every generator conjugated by an orthogonal component reversal →
//        EVERY skew-symmetry, bracket and Casimir pin still passes and ONLY
//        THE EXP-PIN catches it (worst deviation ~1.5 against a 1e-10
//        threshold). That last run is the whole argument for the keystone:
//        the exact algebra cannot see a basis drift, because a drifted basis
//        still satisfies the algebra. Only comparing against the harmonics'
//        own rotation action can.
//
// 5. THE DIFFERENTIAL. Production runs the engine only where composition has
//    already said no, so the two verdicts are never compared on the same body.
//    Here they are: bodies composition ACCEPTS are forcibly engine-checked
//    through `MLEquiv.engineVerdict`, and the verdicts must agree.
module Blade.Tests.LieTablesReview

open System
open Blade.Ast
open Blade.Tests.TestHarness

module LD = Blade.ML.LieDischarge
module PX = Blade.ML.PolyExtract
module MLS = Blade.ML.Spec

// ---------------------------------------------------------------------------
// Real solid spherical harmonics — an INDEPENDENT transcription of the
// ml-spec formulas (BladeML/SphericalHarmonics.fs is the shipped twin; this
// assembly does not reference that project). Component order within degree l
// is m = −l .. l (0-based c = m + l), the order every irreps buffer uses.
// ---------------------------------------------------------------------------

let private factF : float [] =
    let f = Array.zeroCreate 32
    f.[0] <- 1.0
    for n in 1 .. 31 do f.[n] <- f.[n - 1] * float n
    f

let private shEvalUpTo (lmax: int) (x: float) (y: float) (z: float) : float [][] =
    let r2 = x * x + y * y + z * z
    // A.[m] + i·B.[m] = (x + iy)^m
    let a = Array.zeroCreate (lmax + 1)
    let b = Array.zeroCreate (lmax + 1)
    a.[0] <- 1.0
    for m in 1 .. lmax do
        a.[m] <- x * a.[m - 1] - y * b.[m - 1]
        b.[m] <- x * b.[m - 1] + y * a.[m - 1]
    // Homogenized sin-free associated Legendre (no Condon–Shortley phase —
    // that is exactly what `uMatrix` compensates for on the complex side).
    let p = Array.init (lmax + 1) (fun l -> Array.zeroCreate (l + 1))
    p.[0].[0] <- 1.0
    for m in 0 .. lmax - 1 do
        p.[m + 1].[m + 1] <- float (2 * m + 1) * p.[m].[m]
        p.[m + 1].[m] <- float (2 * m + 1) * z * p.[m].[m]
    for m in 0 .. lmax do
        for l in m + 2 .. lmax do
            p.[l].[m] <-
                (float (2 * l - 1) * z * p.[l - 1].[m] - float (l + m - 1) * r2 * p.[l - 2].[m])
                / float (l - m)
    let fourPi = 4.0 * Math.PI
    Array.init (lmax + 1) (fun l ->
        let out = Array.zeroCreate (2 * l + 1)
        out.[l] <- sqrt (float (2 * l + 1) / fourPi) * p.[l].[0]
        for m in 1 .. l do
            let n = sqrt (float (2 * l + 1) / (2.0 * Math.PI) * factF.[l - m] / factF.[l + m])
            out.[l + m] <- n * p.[l].[m] * a.[m]
            out.[l - m] <- n * p.[l].[m] * b.[m]
        out)

let private shEval (l: int) (v: float []) : float [] = (shEvalUpTo l v.[0] v.[1] v.[2]).[l]

// ---------------------------------------------------------------------------
// Small dense float linear algebra (local: the compiler assembly has none)
// ---------------------------------------------------------------------------

let private matMul (a: float [][]) (b: float [][]) : float [][] =
    let n = a.Length
    let m = b.[0].Length
    let k = b.Length
    Array.init n (fun i ->
        Array.init m (fun j ->
            let mutable acc = 0.0
            for t in 0 .. k - 1 do acc <- acc + a.[i].[t] * b.[t].[j]
            acc))

let private matVec (a: float [][]) (v: float []) : float [] =
    a |> Array.map (fun row -> Array.fold2 (fun acc x y -> acc + x * y) 0.0 row v)

let private ident (n: int) : float [][] =
    Array.init n (fun i -> Array.init n (fun j -> if i = j then 1.0 else 0.0))

let private matAdd (a: float [][]) (b: float [][]) =
    Array.init a.Length (fun i -> Array.init a.[0].Length (fun j -> a.[i].[j] + b.[i].[j]))

let private matScale (c: float) (a: float [][]) =
    a |> Array.map (Array.map (fun x -> c * x))

let private maxAbsDiff (a: float [][]) (b: float [][]) =
    let mutable d = 0.0
    for i in 0 .. a.Length - 1 do
        for j in 0 .. a.[i].Length - 1 do
            d <- max d (abs (a.[i].[j] - b.[i].[j]))
    d

/// Gauss–Jordan inverse with partial pivoting. `None` when the pivot collapses
/// (the caller retries with fresh samples, exactly as `Rotations.wignerD` does).
let private inverse (m0: float [][]) : float [][] option =
    let n = m0.Length
    let a = m0 |> Array.map Array.copy
    let inv = ident n
    let mutable ok = true
    for col in 0 .. n - 1 do
        if ok then
            let mutable piv = col
            for r in col .. n - 1 do
                if abs a.[r].[col] > abs a.[piv].[col] then piv <- r
            if abs a.[piv].[col] < 1e-9 then ok <- false
            else
                let t = a.[col] in a.[col] <- a.[piv]; a.[piv] <- t
                let t2 = inv.[col] in inv.[col] <- inv.[piv]; inv.[piv] <- t2
                let d = a.[col].[col]
                for j in 0 .. n - 1 do
                    a.[col].[j] <- a.[col].[j] / d
                    inv.[col].[j] <- inv.[col].[j] / d
                for r in 0 .. n - 1 do
                    if r <> col && abs a.[r].[col] > 0.0 then
                        let f = a.[r].[col]
                        for j in 0 .. n - 1 do
                            a.[r].[j] <- a.[r].[j] - f * a.[col].[j]
                            inv.[r].[j] <- inv.[r].[j] - f * inv.[col].[j]
    if ok then Some inv else None

/// exp(A) by scaling-and-squaring with a degree-20 Taylor core. At the norms
/// in play (|θ|·‖A‖ ≲ 15) the squaring count keeps the Taylor argument under
/// 1/16, where 20 terms are far past double precision.
let private matExp (a: float [][]) : float [][] =
    let n = a.Length
    let nrm =
        a |> Array.fold (fun acc row -> max acc (row |> Array.sumBy abs)) 0.0
    let s = if nrm <= 0.0625 then 0 else int (ceil (log (nrm / 0.0625) / log 2.0))
    let scaled = matScale (1.0 / (2.0 ** float s)) a
    let mutable term = ident n
    let mutable acc = ident n
    for k in 1 .. 20 do
        term <- matScale (1.0 / float k) (matMul term scaled)
        acc <- matAdd acc term
    for _ in 1 .. s do acc <- matMul acc acc
    acc

// The house rotation discipline: R = Rz(0.7)·Ry(1.1), the composition every
// shipped certificate pin (ml-equiv/018 and its descendants) is evaluated at.
let private rotZ (t: float) : float [][] =
    [| [| cos t; -sin t; 0.0 |]; [| sin t; cos t; 0.0 |]; [| 0.0; 0.0; 1.0 |] |]

let private rotY (t: float) : float [][] =
    [| [| cos t; 0.0; sin t |]; [| 0.0; 1.0; 0.0 |]; [| -sin t; 0.0; cos t |] |]

let private rotX (t: float) : float [][] =
    [| [| 1.0; 0.0; 0.0 |]; [| 0.0; cos t; -sin t |]; [| 0.0; sin t; cos t |] |]

/// The real Wigner matrix D_l(R) DEFINED by Y_l(R·v) = D_l(R)·Y_l(v) — the
/// same definition, and the same fit-and-verify recipe, as
/// `Rotations.wignerD`. This is the object the shipped certificates are about.
let private wignerDLocal (l: int) (r: float [][]) : float [][] =
    if l = 0 then [| [| 1.0 |] |]
    else
        let n = 2 * l + 1
        let mutable result : float [][] = null
        let mutable attempt = 0
        while isNull (box result) && attempt < 12 do
            let rng = Random(4177 + 31 * l + attempt)
            let unit () =
                let z = 2.0 * rng.NextDouble() - 1.0
                let phi = rng.NextDouble() * 2.0 * Math.PI
                let s = sqrt (max 0.0 (1.0 - z * z))
                [| s * cos phi; s * sin phi; z |]
            let pts = Array.init n (fun _ -> unit ())
            // Columns are the sample harmonic vectors: Mr = D·M.
            let m = Array.init n (fun i -> Array.init n (fun j -> (shEval l pts.[j]).[i]))
            let mr = Array.init n (fun i -> Array.init n (fun j -> (shEval l (matVec r pts.[j])).[i]))
            match inverse m with
            | None -> ()
            | Some minv ->
                let d = matMul mr minv
                let mutable resid = 0.0
                for _ in 1 .. 4 do
                    let v = unit ()
                    let lhs = shEval l (matVec r v)
                    let rhs = matVec d (shEval l v)
                    for i in 0 .. n - 1 do resid <- max resid (abs (lhs.[i] - rhs.[i]))
                if resid < 1e-9 then result <- d
            attempt <- attempt + 1
        if isNull (box result) then failwithf "Test_LieTables: could not fit D for l = %d" l
        result

// ---------------------------------------------------------------------------
// Radical matrix helpers — THE PIN LAYER (this is what `Radical.mul` is for)
// ---------------------------------------------------------------------------

let private rZeroMat (n: int) = Array.init n (fun _ -> Array.create n LD.Radical.zero)

let private rMatMul (a: LD.Radical [][]) (b: LD.Radical [][]) : LD.Radical [][] =
    let n = a.Length
    Array.init n (fun i ->
        Array.init n (fun j ->
            let mutable acc = LD.Radical.zero
            for k in 0 .. n - 1 do
                if not (LD.Radical.isZero a.[i].[k]) && not (LD.Radical.isZero b.[k].[j]) then
                    acc <- LD.Radical.add acc (LD.Radical.mul a.[i].[k] b.[k].[j])
            acc))

let private rMatSub (a: LD.Radical [][]) (b: LD.Radical [][]) : LD.Radical [][] =
    Array.init a.Length (fun i -> Array.init a.[i].Length (fun j -> LD.Radical.sub a.[i].[j] b.[i].[j]))

let private rMatAdd (a: LD.Radical [][]) (b: LD.Radical [][]) : LD.Radical [][] =
    Array.init a.Length (fun i -> Array.init a.[i].Length (fun j -> LD.Radical.add a.[i].[j] b.[i].[j]))

let private rMatIsZero (a: LD.Radical [][]) =
    a |> Array.forall (Array.forall LD.Radical.isZero)

let private rMatFloat (a: LD.Radical [][]) : float [][] =
    a |> Array.map (Array.map LD.Radical.toFloat)

// ---------------------------------------------------------------------------
// Running the ENGINE on a source string (layers 3-5)
// ---------------------------------------------------------------------------

/// The conjunct normalization MLElaborate performs at its own seam
/// (`ml.equiv` -> `__ml_equiv`); mirrored here because this block calls the
/// judgment directly rather than through the elaborator.
let private normalizeConjuncts (decls: Located<Decl> list) =
    decls
    |> List.map (fun d ->
        match d.Value with
        | DeclFunction fd ->
            let w =
                fd.WhereClause
                |> Option.map (fun w ->
                    { w with
                        Custom =
                            w.Custom
                            |> List.map (fun (n, args) ->
                                ((if n = "ml.equiv" then "__ml_equiv" else n), args)) })
            { d with Value = DeclFunction { fd with WhereClause = w } }
        | _ -> d)

let private mlAliases = Set.ofList [ "ml" ]

type private Judged = {
    /// [] = the composition judgment accepts (the production verdict).
    Composition: string list
    /// `None` = the engine declines (extraction refusal / cap); `Some (Ok ())`
    /// = the polynomial discharges; `Some (Error m)` = it does not.
    Engine: Result<unit, string> option
}

let private judgeSource (src: string) (fname: string) : Judged =
    match Blade.Parser.parseProgram src with
    | Error e -> failwithf "Test_LieTables: parse failed: %s" e.Message
    | Ok prog ->
        let decls = prog.Modules |> List.collect (fun m -> m.Decls) |> normalizeConjuncts
        match Blade.StaticEval.resolveStatics decls with
        | Error e -> failwithf "Test_LieTables: static resolution failed: %s" e
        | Ok (statics, _) ->
            match Blade.ML.Equiv.buildCertTable statics decls with
            | Error d -> failwithf "Test_LieTables: cert table failed: %s" d.Message
            | Ok certs ->
                let fd =
                    decls
                    |> List.tryPick (fun d ->
                        match d.Value with
                        | DeclFunction f when f.Name = fname -> Some f
                        | _ -> None)
                match fd with
                | None -> failwithf "Test_LieTables: no function '%s' in the source" fname
                | Some fd ->
                    let grp = (Map.find fname certs).Group
                    { Composition =
                        Blade.ML.Equiv.judgeFunction grp certs statics mlAliases fd
                        |> List.map (fun d -> d.Message)
                      Engine = Blade.ML.Equiv.engineVerdict certs statics fd }

// The standing sources for layers 3-5. Written once, reused by the positive
// pins, the negative controls and the differential.
let private srcHeader = """import ml as ml
let static V = [(1, 1, 1)]
let static VE = [(1, 0, 1)]
let static PS = [(0, 1, 1)]
let static Q = [(2, 0, 1)]
let static SV = [(0, 0, 1), (1, 1, 1)]
"""

let private src (body: string) = srcHeader + body

// The triple product, spelled once per return shape. Module-level so the
// `"""` terminators sit at column 0 without closing the enclosing binding.
let private tripleScalarTemplate : Printf.StringFormat<string -> string -> string -> string> = """
function %s(u: Array<Float like IrrepsIdx<V>>, v: Array<Float like IrrepsIdx<V>>, w: Array<Float like IrrepsIdx<V>>)
            where ml.equiv(%s) -> %s =
    u(0)*(v(1)*w(2) - v(2)*w(1)) + u(1)*(v(2)*w(0) - v(0)*w(2)) + u(2)*(v(0)*w(1) - v(1)*w(0))
"""

let private tripleOddTemplate : Printf.StringFormat<string -> string> = """
function tri_odd(u: Array<Float like IrrepsIdx<V>>, v: Array<Float like IrrepsIdx<V>>, w: Array<Float like IrrepsIdx<V>>)
            where ml.equiv(%s) -> Array<Float like IrrepsIdx<PS>> =
    [u(0)*(v(1)*w(2) - v(2)*w(1)) + u(1)*(v(2)*w(0) - v(0)*w(2)) + u(2)*(v(0)*w(1) - v(1)*w(0))]
"""

// ---------------------------------------------------------------------------

let runLieTablesTests () : BlockResult =
    printHeader "Lie Tables (so(3) generators + radical discharge)"
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
    // LAYER 0 — the radical vector module itself
    // =======================================================================
    check "squarefree: 12 = 2^2·3, 8 = 2^2·2, 18 = 3^2·2, 50 = 5^2·2, 4 = 2^2·1, 1 = 1^2·1"
          (LD.squarefree 12 = (2, 3) && LD.squarefree 8 = (2, 2) && LD.squarefree 18 = (3, 2)
           && LD.squarefree 50 = (5, 2) && LD.squarefree 4 = (2, 1) && LD.squarefree 1 = (1, 1))
          ""
    let half = PX.Rat.make (bigint 1) (bigint 2)
    check "ofSqrt normalizes: (1/2)·sqrt(12) = 1·sqrt(3), key 3"
          (LD.Radical.ofSqrt half 12 = Map.ofList [ (3, PX.Rat.one) ]) ""
    check "ofSqrt at n = 0 and q = 0 is the zero vector"
          (LD.Radical.isZero (LD.Radical.ofSqrt half 0) && LD.Radical.isZero (LD.Radical.ofSqrt PX.Rat.zero 12)) ""
    check "add prunes an exact cancellation (sqrt(3) - sqrt(3) = 0, not a zero entry)"
          (LD.Radical.isZero (LD.Radical.sub (LD.Radical.ofSqrt PX.Rat.one 3) (LD.Radical.ofSqrt PX.Rat.one 3))) ""
    check "PIN-LAYER mul: sqrt(2)·sqrt(6) = 2·sqrt(3), sqrt(3)·sqrt(3) = 3"
          (LD.Radical.mul (LD.Radical.ofSqrt PX.Rat.one 2) (LD.Radical.ofSqrt PX.Rat.one 6)
             = Map.ofList [ (3, PX.Rat.ofInt 2) ]
           && LD.Radical.mul (LD.Radical.ofSqrt PX.Rat.one 3) (LD.Radical.ofSqrt PX.Rat.one 3)
             = Map.ofList [ (1, PX.Rat.ofInt 3) ])
          ""
    check "render: 0 / -1 / 3/2*sqrt(2) / mixed"
          (LD.Radical.render LD.Radical.zero = "0"
           && LD.Radical.render (LD.Radical.ofInt -1) = "-1"
           && LD.Radical.render (LD.Radical.ofSqrt (PX.Rat.make (bigint 3) (bigint 2)) 2) = "3/2*sqrt(2)"
           && LD.Radical.render (LD.Radical.add (LD.Radical.ofInt 1) (LD.Radical.ofSqrt PX.Rat.one 3))
                = "1 + 1*sqrt(3)")
          ""

    // =======================================================================
    // LAYER 1 — THE EXP-PIN (the keystone)
    // =======================================================================
    // For each l and each axis: float-assemble the exact table, exponentiate
    // at angle θ, and compare against the Wigner matrix fit from the
    // harmonics' own rotation action. A convention drift anywhere between
    // uMatrix, the ladder normalization, the realification and the row order
    // shows up here and nowhere else.
    let angles = [ 0.3; 0.7; 1.1; -0.9; 2.4 ]
    let axisData =
        [ (LD.Lx, "Lx", rotX); (LD.Ly, "Ly", rotY); (LD.Lz, "Lz", rotZ) ]
    let mutable expWorstAll = 0.0
    for l in 0 .. 3 do
        let mutable worstL = 0.0
        for (ax, axn, rot) in axisData do
            let a = rMatFloat (LD.blockGenerator ax l)
            let mutable worst = 0.0
            for th in angles do
                let d = wignerDLocal l (rot th)
                let e = matExp (matScale th a)
                worst <- max worst (maxAbsDiff d e)
            check (sprintf "EXP-PIN l=%d %s: exp(theta·A) = D_l(R_%s(theta)) at 5 angles" l axn (axn.Substring 1))
                  (worst < 1e-10) (sprintf "worst %.3g" worst)
            worstL <- max worstL worst
        // The house composition R = Rz(0.7)·Ry(1.1), the angle pair every
        // shipped certificate pin uses.
        let rHouse = matMul (rotZ 0.7) (rotY 1.1)
        let dHouse = wignerDLocal l rHouse
        let eHouse =
            matMul (matExp (matScale 0.7 (rMatFloat (LD.blockGenerator LD.Lz l))))
                   (matExp (matScale 1.1 (rMatFloat (LD.blockGenerator LD.Ly l))))
        let wh = maxAbsDiff dHouse eHouse
        check (sprintf "EXP-PIN l=%d: exp(0.7·Lz)·exp(1.1·Ly) = D_l(Rz(0.7)·Ry(1.1)) [the house R]" l)
              (wh < 1e-10) (sprintf "worst %.3g" wh)
        worstL <- max worstL wh
        expWorstAll <- max expWorstAll worstL
        printfn "         l=%d worst exp-pin deviation: %.3g" l worstL
    // The block-diagonal assembly is the same claim one level up: a multi-block
    // spec's generator must exponentiate to `applyRep`'s block-diagonal action.
    let specMix : MLS.Spec = [ { L = 0; Parity = 0; Mult = 2 }; { L = 1; Parity = 1; Mult = 1 }; { L = 2; Parity = 0; Mult = 2 } ]
    let rHouse = matMul (rotZ 0.7) (rotY 1.1)
    let dBlock =
        let n = MLS.totalDim specMix
        let out = Array.init n (fun _ -> Array.zeroCreate n)
        let starts = MLS.blockStarts specMix |> List.toArray
        specMix
        |> List.iteri (fun bi (e: MLS.SpecEntry) ->
            let d = wignerDLocal e.L rHouse
            let dim = 2 * e.L + 1
            for copy in 0 .. e.Mult - 1 do
                let st = starts.[bi] + copy * dim
                for i in 0 .. dim - 1 do
                    for j in 0 .. dim - 1 do out.[st + i].[st + j] <- d.[i].[j])
        out
    let eBlock =
        matMul (matExp (matScale 0.7 (rMatFloat (LD.specGenerator LD.Lz specMix))))
               (matExp (matScale 1.1 (rMatFloat (LD.specGenerator LD.Ly specMix))))
    let wb = maxAbsDiff dBlock eBlock
    check "EXP-PIN block assembly: specGenerator over [(0,e,2),(1,o,1),(2,e,2)] exponentiates to applyRep's block-diagonal D at the house R"
          (wb < 1e-10) (sprintf "worst %.3g" wb)
    printfn "         exp-pin worst over all l <= 3 (three axes, five angles, house R, block assembly): %.3g"
            (max expWorstAll wb)

    // =======================================================================
    // LAYER 2 — exact table pins (l <= 4)
    // =======================================================================
    for l in 0 .. 4 do
        let gx = LD.blockGenerator LD.Lx l
        let gy = LD.blockGenerator LD.Ly l
        let gz = LD.blockGenerator LD.Lz l
        let d = 2 * l + 1
        // Skew-symmetry, per RADICAL COMPONENT (not per float): A[i][j] must be
        // the exact negation of A[j][i] as a vector.
        let skew =
            [ gx; gy; gz ]
            |> List.forall (fun g ->
                Seq.forall (fun i ->
                    Seq.forall (fun j -> g.[i].[j] = LD.Radical.neg g.[j].[i]) (seq { 0 .. d - 1 }))
                    (seq { 0 .. d - 1 }))
        check (sprintf "l=%d: all three generators exactly skew-symmetric (componentwise)" l) skew ""
        // The so(3) brackets, exactly — this is where Radical.mul earns its
        // keep, and it is the check that would catch a wrong ladder sign that
        // skew-symmetry alone cannot see.
        let br a b c = rMatIsZero (rMatSub (rMatSub (rMatMul a b) (rMatMul b a)) c)
        check (sprintf "l=%d: [Lx,Ly] = Lz, [Ly,Lz] = Lx, [Lz,Lx] = Ly EXACTLY" l)
              (br gx gy gz && br gy gz gx && br gz gx gy) ""
        // Casimir: Lx² + Ly² + Lz² = −l(l+1)·I, exactly.
        let cas = rMatAdd (rMatAdd (rMatMul gx gx) (rMatMul gy gy)) (rMatMul gz gz)
        let want =
            Array.init d (fun i -> Array.init d (fun j -> if i = j then LD.Radical.ofInt (-(l * (l + 1))) else LD.Radical.zero))
        check (sprintf "l=%d: Casimir Lx^2+Ly^2+Lz^2 = -%d·I EXACTLY" l (l * (l + 1)))
              (rMatIsZero (rMatSub cas want)) ""
    // The coded-convention shapes, pinned as data so a table edit has to
    // confront them: L_z integer, L_x/L_y at l = 2 carrying sqrt(3) and 1.
    let gz1 = LD.blockGenerator LD.Lz 1
    check "l=1 Lz is the integer block [[0,-m],[m,0]]: A[2][0] = -1, A[0][2] = +1, m=0 row zero"
          (gz1.[2].[0] = LD.Radical.ofInt -1 && gz1.[0].[2] = LD.Radical.ofInt 1
           && gz1.[1] |> Array.forall LD.Radical.isZero) ""
    let gy2 = LD.blockGenerator LD.Ly 2
    check "l=2 Ly: the m=0 seam carries sqrt(3) = (1/2)sqrt(2·l(l+1)) and the rest is integral"
          (gy2.[2].[3] = LD.Radical.ofSqrt (PX.Rat.ofInt -1) 3
           && gy2.[3].[2] = LD.Radical.ofSqrt PX.Rat.one 3
           && gy2.[3].[4] = LD.Radical.ofInt -1
           && gy2.[4].[3] = LD.Radical.ofInt 1)
          (sprintf "A[2][3] = %s" (LD.Radical.render gy2.[2].[3]))
    // Parity is separate from the algebra — the whole reason O(3) is 3 + 1.
    // [(0,e,2),(1,o,1),(2,e,2)] = 2 + 3 + 10 = 15 components; only the l=1
    // block is odd, and the parity is per COMPONENT, not per block.
    check "specParity reads block parities per component, multiplicity-aware"
          (LD.specParity specMix
             = [| 0; 0; 1; 1; 1; 0; 0; 0; 0; 0; 0; 0; 0; 0; 0 |])
          (sprintf "%A" (LD.specParity specMix))
    check "the so(3) tables do NOT depend on parity (the -I action is separate)"
          (LD.specGenerator LD.Lx [ { L = 1; Parity = 0; Mult = 1 } ]
             = LD.specGenerator LD.Lx [ { L = 1; Parity = 1; Mult = 1 } ]) ""

    // =======================================================================
    // LAYER 3 — known answers, as verdicts
    // =======================================================================
    let dotSrc =
        src """
function dot3(u: Array<Float like IrrepsIdx<V>>, v: Array<Float like IrrepsIdx<V>>)
              where ml.equiv(O3) -> Float =
    u(0) * v(0) + u(1) * v(1) + u(2) * v(2)
"""
    let dotJ = judgeSource dotSrc "dot3"
    check "dot(u,v) of two ODD vectors is an O3 INVARIANT (the engine certifies what composition's raw-index rule cannot)"
          (dotJ.Engine = Some (Ok ())) ""
    let crossBody = """
function cross3(u: Array<Float like IrrepsIdx<V>>, v: Array<Float like IrrepsIdx<V>>)
                where ml.equiv(O3) -> Array<Float like IrrepsIdx<VE>> =
    [u(1) * v(2) - u(2) * v(1), u(2) * v(0) - u(0) * v(2), u(0) * v(1) - u(1) * v(0)]
"""
    check "cross(u,v) of two ODD vectors certifies at an EVEN l=1 target (a pseudovector)"
          ((judgeSource (src crossBody) "cross3").Engine = Some (Ok ())) ""
    let crossOddBody = """
function cross_odd(u: Array<Float like IrrepsIdx<V>>, v: Array<Float like IrrepsIdx<V>>)
                   where ml.equiv(O3) -> Array<Float like IrrepsIdx<V>> =
    [u(1) * v(2) - u(2) * v(1), u(2) * v(0) - u(0) * v(2), u(0) * v(1) - u(1) * v(0)]
"""
    let crossOdd = (judgeSource (src crossOddBody) "cross_odd").Engine
    check "the SAME cross product at an ODD target is refused, and only -I sees it"
          (match crossOdd with
           | Some (Error m) -> m.Contains "IS SO(3)-equivariant" && m.Contains "inversion identity"
           | _ -> false)
          ""
    check "and it certifies under equiv(SO3), where there is no component group"
          ((judgeSource (src (crossOddBody.Replace("ml.equiv(O3)", "ml.equiv(SO3)").Replace("cross_odd", "cross_so3"))) "cross_so3").Engine
             = Some (Ok ())) ""

    // THE TRIPLE-PRODUCT TRIPLE — the -I rule as three verdicts.
    let tripleBody (name: string) (grp: string) (ret: string) =
        sprintf tripleScalarTemplate name grp ret
    let tripleOdd (grp: string) = sprintf tripleOddTemplate grp
    check "TRIPLE 1/3: u.(v x w) certifies under SO3 declared EVEN (a plain Float return)"
          ((judgeSource (src (tripleBody "tri_so3_even" "SO3" "Float")) "tri_so3_even").Engine = Some (Ok ())) ""
    check "TRIPLE 2/3: the same body certifies under SO3 declared ODD (l=0 odd block)"
          ((judgeSource (src (tripleOdd "SO3")) "tri_odd").Engine = Some (Ok ())) ""
    check "TRIPLE 3/3a: under O3 it certifies ONLY as (0, odd) — the pseudoscalar reading"
          ((judgeSource (src (tripleOdd "O3")) "tri_odd").Engine = Some (Ok ())) ""
    let tripleEvenO3 = (judgeSource (src (tripleBody "tri_o3_even" "O3" "Float")) "tri_o3_even").Engine
    check "TRIPLE 3/3b: under O3 declared (0, even) it is REFUSED, by -I and by nothing else"
          (match tripleEvenO3 with
           | Some (Error m) ->
               m.Contains "IS SO(3)-equivariant" && m.Contains "ml.equiv(SO3)"
               && m.Contains "output component 0"
           | _ -> false)
          ""

    // THE STAGE THESIS PIN. A RATIONAL body on an l = 2 rep: every generator
    // entry that enters the check carries sqrt(3) (the m = 0 seam's
    // (1/2)sqrt(2·l(l+1)) = sqrt(3) at l = 2), the defect's coefficients are
    // genuine radical vectors, and componentwise-zero accepts exactly. This is
    // what "the linearity resolution beats the l <= 1 restriction" means.
    let radialBody = """
function radial(x: Array<Float like IrrepsIdx<Q>>)
                where ml.equiv(O3) -> Array<Float like IrrepsIdx<Q>> = {
    let r2 = x(0)*x(0) + x(1)*x(1) + x(2)*x(2) + x(3)*x(3) + x(4)*x(4)
    [r2*x(0), r2*x(1), r2*x(2), r2*x(3), r2*x(4)]
}
"""
    check "THESIS PIN: |x|^2·x on [(2,e,1)] certifies under O3 — rational body, sqrt(3) generator entries in the check"
          ((judgeSource (src radialBody) "radial").Engine = Some (Ok ())) ""
    let l2Irrational =
        let g = LD.blockGenerator LD.Ly 2
        g |> Array.exists (Array.exists (fun r -> r |> Map.exists (fun k _ -> k > 1)))
    check "  ...and the l=2 table really is irrational (so the pin is not vacuous)" l2Irrational ""

    // =======================================================================
    // LAYER 4 — negative controls, live-failing then standing
    // =======================================================================
    // (a) one coefficient perturbed by an EXACT 1/7 (written as a literal
    //     division, so the extractor reads 1/7 and not a truncated decimal).
    let perturbed = """
function radial_bad(x: Array<Float like IrrepsIdx<Q>>)
                    where ml.equiv(O3) -> Array<Float like IrrepsIdx<Q>> = {
    let r2 = x(0)*x(0) + x(1)*x(1) + x(2)*x(2) + x(3)*x(3) + x(4)*x(4)
    [(1.0 + 1.0/7.0)*r2*x(0), r2*x(1), r2*x(2), r2*x(3), r2*x(4)]
}
"""
    let pv = (judgeSource (src perturbed) "radial_bad").Engine
    check "NEGATIVE (1/7): a single coefficient off by an exact 1/7 is refused, naming a Lie generator"
          (match pv with
           | Some (Error m) ->
               m.Contains "not O3-equivariant" && m.Contains "Lie generator"
               && (m.Contains "Lx" || m.Contains "Ly" || m.Contains "Lz")
           | _ -> false)
          (match pv with Some (Error m) -> m.Substring(0, min 130 m.Length) | _ -> "no refusal")
    check "  ...and it is NOT reported as a near miss (1/7 is not a rounding artefact)"
          (match pv with Some (Error m) -> not (m.Contains "NEAR MISS") | _ -> false) ""

    // (b) the truncated 1/sqrt(3). The l=1 x l=1 -> l=2 quadratic has an
    //     IRRATIONAL coefficient ratio in the shipped real basis, so no exact
    //     spelling of it exists — which is precisely the case §3.5 hands to
    //     the synthesized (Schur-certified) basis. Writing 0.5773502 for
    //     1/sqrt(3) must produce the near-miss note pointing at both hatches.
    let nearMissSrc = """
function quad(x: Array<Float like IrrepsIdx<V>>)
              where ml.equiv(O3) -> Array<Float like IrrepsIdx<Q>> =
    [x(0)*x(2),
     x(0)*x(1),
     0.5773502 * (x(1)*x(1) - 0.5*x(0)*x(0) - 0.5*x(2)*x(2)),
     x(1)*x(2),
     0.5*(x(2)*x(2) - x(0)*x(0))]
"""
    let nm = (judgeSource (src nearMissSrc) "quad").Engine
    check "NEGATIVE (near miss): 0.5773502 written for 1/sqrt(3) is refused WITH the near-miss note"
          (match nm with
           | Some (Error m) ->
               m.Contains "NEAR MISS" && m.Contains "3.0 / 10.0 IS 3/10"
               && m.Contains "ml.derive_linear"
           | _ -> false)
          (match nm with Some (Error m) -> (if m.Contains "NEAR MISS" then m.Substring(m.IndexOf "NEAR MISS", 60) else "no note") | _ -> "no refusal")
    check "  ...and the residual is a genuine RADICAL vector (a rational part and a sqrt(3) part)"
          (match nm with Some (Error m) -> m.Contains "sqrt(3)" | _ -> false) ""

    // (c) wrong declared parity — already exercised by cross_odd and the
    //     triple; here the OTHER direction, an even input pair claimed odd
    //     out, to pin that -I is checked for INPUT parities too.
    let parityCtl = """
function scale_even(x: Array<Float like IrrepsIdx<VE>>)
                    where ml.equiv(O3) -> Array<Float like IrrepsIdx<V>> =
    [x(0), x(1), x(2)]
"""
    let pc = (judgeSource (src parityCtl) "scale_even").Engine
    check "NEGATIVE (parity): the identity map from an EVEN l=1 block to an ODD one dies at -I"
          (match pc with Some (Error m) -> m.Contains "inversion identity" | _ -> false) ""

    // =======================================================================
    // LAYER 5 — the differential: composition-accepted bodies, engine-checked
    // =======================================================================
    let diffCases =
        [ "add", """
function add2(x: Array<Float like IrrepsIdx<SV>>, y: Array<Float like IrrepsIdx<SV>>)
              where ml.equiv(O3) -> Array<Float like IrrepsIdx<SV>> =
    x + y
"""
          "scale", """
function scale2(x: Array<Float like IrrepsIdx<SV>>)
                where ml.equiv(O3) -> Array<Float like IrrepsIdx<SV>> =
    x * 2.0
"""
          "identity", """
function id2(x: Array<Float like IrrepsIdx<SV>>)
             where ml.equiv(O3) -> Array<Float like IrrepsIdx<SV>> =
    x
"""
          "invariant read", """
function head(x: Array<Float like IrrepsIdx<SV>>)
              where ml.equiv(O3) -> Float =
    x(0)
"""
          "weighted mix", """
function mix(x: Array<Float like IrrepsIdx<SV>>, y: Array<Float like IrrepsIdx<SV>>, s: Float)
             where ml.equiv(O3) -> Array<Float like IrrepsIdx<SV>> =
    x * s + y
""" ]
    let names = [ "add2"; "scale2"; "id2"; "head"; "mix" ]
    List.iter2
        (fun (label, body) name ->
            let j = judgeSource (src body) name
            check (sprintf "DIFFERENTIAL (%s): composition accepts AND the engine independently discharges" label)
                  (j.Composition.IsEmpty && j.Engine = Some (Ok ()))
                  (sprintf "composition %d diags, engine %s"
                       j.Composition.Length
                       (match j.Engine with
                        | Some (Ok ()) -> "accept"
                        | Some (Error _) -> "REFUSE"
                        | None -> "declined")))
        diffCases names
    // The other direction, on the same harness: a body composition REFUSES and
    // the engine also refuses (no verdict is invented on either side).
    let bothRefuse = """
function tilt(x: Array<Float like IrrepsIdx<SV>>)
              where ml.equiv(O3) -> Array<Float like IrrepsIdx<SV>> =
    [x(0), x(2), x(1), x(3)]
"""
    let brj = judgeSource (src bothRefuse) "tilt"
    check "DIFFERENTIAL (both refuse): a component swap is rejected by composition AND by the engine"
          (not brj.Composition.IsEmpty
           && (match brj.Engine with Some (Error _) -> true | _ -> false)) ""

    printFooter "Lie Tables" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "Lie Tables"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
