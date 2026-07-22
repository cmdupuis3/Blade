namespace BladeSgs

/// Physics oracle for the sgs corpus (subgrid-closure discovery arc):
/// synthetic divergence-free fields, box filters, and the exact SGS stress
/// as a central comoment. Prints pins in the copy-pasteable form the corpus
/// tests bake. Run:
///   dotnet run --project sgs -- dump-oracle
///
/// FROZEN FIELD CONFIG (shared by sgs/004 and sgs/005):
///   N = 4 per axis, L = 2pi, h = pi/2, periodic. Four cosine modes, each
///   with amplitude orthogonal to the DISCRETE central-difference wavevector
///   kappa_j = sin(m_j h)/h — so the field is div-free with respect to the
///   grid operator (which is the operator the sgs arc differentiates with),
///   not merely analytically:
///     A: m=(1,0,0)  a=(0.0, 0.8,-0.5)  phi=0.3
///     B: m=(0,1,0)  a=(0.7, 0.0, 0.4)  phi=1.1
///     C: m=(1,3,0)  a=(0.6, 0.6,-0.9)  phi=2.0   (kappa_x = -kappa_y = 2/pi)
///     D: m=(0,0,1)  a=(0.5,-0.6, 0.0)  phi=0.7
///   Sample order within a W=2 tile: t = i*4 + j*2 + k (ascending lex).
module Oracle =

    let private f2s (v: float) : string =
        let s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

    let private arr (name: string) (xs: float[]) =
        printfn "%s = [%s]" name (xs |> Array.map f2s |> String.concat ", ")

    let n = 4
    let h = System.Math.PI / 2.0

    /// The three velocity components at grid point (i, j, k) — these
    /// expressions MIRROR the Blade source of sgs/004 term for term.
    let u0 (x: float) (y: float) (z: float) : float =
        0.7 * cos (y + 1.1) + 0.6 * cos (x + 3.0 * y + 2.0) + 0.5 * cos (z + 0.7)
    let u1 (x: float) (y: float) (z: float) : float =
        0.8 * cos (x + 0.3) + 0.6 * cos (x + 3.0 * y + 2.0) - 0.6 * cos (z + 0.7)
    let u2 (x: float) (y: float) (z: float) : float =
        -0.5 * cos (x + 0.3) + 0.4 * cos (y + 1.1) - 0.9 * cos (x + 3.0 * y + 2.0)

    let private fieldAt (c: int) (i: int) (j: int) (k: int) : float =
        let x = float i * h
        let y = float j * h
        let z = float k * h
        match c with
        | 0 -> u0 x y z
        | 1 -> u1 x y z
        | _ -> u2 x y z

    /// Central-difference divergence at (i,j,k), periodic wrap.
    let private fdDiv (i: int) (j: int) (k: int) : float =
        let w a = ((a % n) + n) % n
        (fieldAt 0 (w (i + 1)) j k - fieldAt 0 (w (i - 1)) j k) / (2.0 * h)
        + (fieldAt 1 i (w (j + 1)) k - fieldAt 1 i (w (j - 1)) k) / (2.0 * h)
        + (fieldAt 2 i j (w (k + 1)) - fieldAt 2 i j (w (k - 1))) / (2.0 * h)

    /// The W=2 tile at the origin, flattened t = i*4 + j*2 + k: 3 rows of 8.
    let private tileRows () : float[][] =
        Array.init 3 (fun c ->
            [| for i in 0 .. 1 do
                 for j in 0 .. 1 do
                   for k in 0 .. 1 do
                     yield fieldAt c i j k |])

    /// Central second comoment (population), packed upper triangle
    /// [(0,0);(0,1);(0,2);(1,1);(1,2);(2,2)]: E[ab] - ma*mb, sums ascending.
    let private tau (rows: float[][]) : float[] * float[] * float[] =
        let t = float rows.[0].Length
        let mean (r: float[]) = Array.sum r / t
        let mu = rows |> Array.map mean
        let prodsum (a: float[]) (b: float[]) =
            let mutable acc = 0.0
            for i in 0 .. a.Length - 1 do
                acc <- acc + a.[i] * b.[i]
            acc
        let pairs = [| (0, 0); (0, 1); (0, 2); (1, 1); (1, 2); (2, 2) |]
        let raw = pairs |> Array.map (fun (a, b) -> prodsum rows.[a] rows.[b] / t)
        let cen = Array.mapi (fun p (a, b) -> raw.[p] - mu.[a] * mu.[b]) pairs
        raw, mu, cen

    let dump () =
        printfn "// ===== sgs physics oracle (fields N=4, W=2 tile) ====="

        // ---- 004: probe values + divergence residuals (TOLERANCE pins:
        // the Blade side evaluates cos in the C++ runtime, which is not
        // bit-identical to .NET libm — verdicts, not exact EXPECTs) ----
        let probes = [| (0, 0, 0); (1, 2, 3); (3, 1, 2) |]
        for (i, j, k) in probes do
            for c in 0 .. 2 do
                printfn "u%d_p%d%d%d = %s" c i j k (f2s (fieldAt c i j k))
        let mutable divMax = 0.0
        for i in 0 .. n - 1 do
            for j in 0 .. n - 1 do
                for k in 0 .. n - 1 do
                    divMax <- max divMax (abs (fdDiv i j k))
        printfn "// 004 max |fd div| over grid = %g (must be ~1e-16)" divMax

        // ---- 005: the tile rows as EXACT literals (everything downstream
        // is +-*/ and pins bit-exactly), both stress routes, Galilean shift ----
        let rows = tileRows ()
        let u0shift = [| 0.3; -0.2; 0.5 |]
        let rowsShift = rows |> Array.mapi (fun c r -> r |> Array.map (fun v -> v + u0shift.[c]))
        arr "tile_u0" rows.[0]
        arr "tile_u1" rows.[1]
        arr "tile_u2" rows.[2]
        arr "tile_v0" rowsShift.[0]
        arr "tile_v1" rowsShift.[1]
        arr "tile_v2" rowsShift.[2]
        let raw, mu, cen = tau rows
        let rawS, muS, cenS = tau rowsShift
        arr "m2_raw" raw
        arr "mu" mu
        arr "tau" cen
        arr "mu_shift" muS
        arr "tau_shift" cenS
        printfn "// 005 raw second moments move O(1) under the boost: |m2_raw - m2_raw_shift|_max = %g"
            (Array.map2 (fun a b -> abs (a - b)) raw rawS |> Array.max)
        printfn "// 005 the CENTRAL comoment does not: |tau - tau_shift|_max = %g"
            (Array.map2 (fun a b -> abs (a - b)) cen cenS |> Array.max)

    /// dump-formers: pins for the sgs.grad / sgs.box_filter / sgs.stress
    /// elaborated ops (corpus sgs/008-009). Prints the FULL N=4 field as a
    /// pasteable rank-4 Blade literal (so the corpus test is trig-free and
    /// pins bit-exactly), gradient probes, all filter cells, and the packed
    /// stress of two tiles.
    let dumpFormers () =
        printfn "// ===== sgs formers oracle (sgs/008-009) ====="
        let lit =
            [ for c in 0 .. 2 ->
                [ for i in 0 .. 3 ->
                    [ for j in 0 .. 3 ->
                        "[" + ([ for k in 0 .. 3 -> f2s (fieldAt c i j k) ] |> String.concat ", ") + "]" ]
                    |> String.concat ", " |> sprintf "[%s]" ]
                |> String.concat ", " |> sprintf "[%s]" ]
            |> String.concat ", " |> sprintf "[%s]"
        printfn "let U: Array<Float64 like Idx<3>, Idx<4>, Idx<4>, Idx<4>> = %s" lit
        // gradient probes: G(c, d) at two points, the fdDiv slots included
        let fdGradAt (i: int) (j: int) (k: int) : float[][] =
            let w a = ((a % n) + n) % n
            Array.init 3 (fun c ->
                [| (fieldAt c (w (i + 1)) j k - fieldAt c (w (i - 1)) j k) / (2.0 * h)
                   (fieldAt c i (w (j + 1)) k - fieldAt c i (w (j - 1)) k) / (2.0 * h)
                   (fieldAt c i j (w (k + 1)) - fieldAt c i j (w (k - 1))) / (2.0 * h) |])
        for (i, j, k) in [ (0, 0, 0); (1, 2, 3) ] do
            let g = fdGradAt i j k
            for c in 0 .. 2 do
                for d in 0 .. 2 do
                    printfn "g%d%d_%d%d%d = %s" c d i j k (f2s g.[c].[d])
        // box_filter: all 24 tile means (W = 2)
        for c in 0 .. 2 do
            for ti in 0 .. 1 do
                for tj in 0 .. 1 do
                    for tk in 0 .. 1 do
                        let mutable s = 0.0
                        for di in 0 .. 1 do
                            for dj in 0 .. 1 do
                                for dk in 0 .. 1 do
                                    s <- s + fieldAt c (2 * ti + di) (2 * tj + dj) (2 * tk + dk)
                        printfn "f%d_%d%d%d = %s" c ti tj tk (f2s (s / 8.0))
        // stress of tiles (0,0,0) (= the 005 tau pins) and (1,1,1)
        let tileTau (ti: int) (tj: int) (tk: int) =
            let rows =
                Array.init 3 (fun c ->
                    [| for di in 0 .. 1 do
                         for dj in 0 .. 1 do
                           for dk in 0 .. 1 do
                             yield fieldAt c (2 * ti + di) (2 * tj + dj) (2 * tk + dk) |])
            let _, _, cen = tau rows
            cen
        arr "tau000" (tileTau 0 0 0)
        arr "tau111" (tileTau 1 1 1)
