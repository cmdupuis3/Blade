/// Prints oracle values for the fixed corpus fixtures (tests/corpus/math/)
/// in EXPECT-ready form. Run: dotnet run --project math/BladeMath.fsproj
module BladeMath.OracleDump

open System.Globalization

let private fmt (x: float) = x.ToString("G17", CultureInfo.InvariantCulture)
let private fmtArr (xs: float[]) = "[" + (xs |> Array.map fmt |> String.concat ", ") + "]"
let private flat2 (a: float[,]) =
    [| for i in 0 .. Array2D.length1 a - 1 do
         for j in 0 .. Array2D.length2 a - 1 -> a.[i, j] |]

let dumpAll () =
    // --- svd 4x3 fixture (corpus math/014, sweeps = 10) ---
    let a = array2D [ [1.0; 2.0; 3.0]; [4.0; 5.0; 6.0]; [7.0; 8.0; 10.0]; [2.0; 1.0; 0.0] ]
    let (u, s, v) = Jacobi.svd 10 a
    printfn "svd 4x3 fixture (sweeps=10):"
    printfn "  S = %s" (fmtArr s)
    printfn "  U = %s" (fmtArr (flat2 u))
    printfn "  V = %s" (fmtArr (flat2 v))
    let mutable resid = 0.0
    for i in 0 .. 3 do
        for j in 0 .. 2 do
            let mutable acc = 0.0
            for k in 0 .. 2 do acc <- acc + u.[i, k] * s.[k] * v.[j, k]
            let d = acc - a.[i, j]
            resid <- resid + d * d
    printfn "  resid = %s" (fmt resid)

    // --- svd known 2x2 [[3,0],[4,5]] (corpus math/013): σ = 3√5, √5 ---
    let a2 = array2D [ [3.0; 0.0]; [4.0; 5.0] ]
    let (_, s2, _) = Jacobi.svd 10 a2
    printfn "svd 2x2 [[3,0],[4,5]]:"
    printfn "  S = %s (analytic: 3*sqrt5 = %s, sqrt5 = %s)" (fmtArr s2) (fmt (3.0 * sqrt 5.0)) (fmt (sqrt 5.0))

    // --- eigh 3x3 tridiagonal (corpus math/021): analytic 2±√2, 2 ---
    let s3 = array2D [ [2.0; 1.0; 0.0]; [1.0; 2.0; 1.0]; [0.0; 1.0; 2.0] ]
    let (q3, l3) = Jacobi.eigh 10 s3
    printfn "eigh 3x3 tridiag:"
    printfn "  LAM = %s (analytic: %s, 2, %s)" (fmtArr l3) (fmt (2.0 + sqrt 2.0)) (fmt (2.0 - sqrt 2.0))
    printfn "  Q = %s" (fmtArr (flat2 q3))

    // --- eigh 4x4 fixture (corpus math/02x) ---
    let s4 = array2D [ [4.0; 1.0; 0.0; 2.0]
                       [1.0; 3.0; 1.0; 0.0]
                       [0.0; 1.0; 2.0; 1.0]
                       [2.0; 0.0; 1.0; 5.0] ]
    let (_, l4) = Jacobi.eigh 10 s4
    printfn "eigh 4x4 fixture:"
    printfn "  LAM = %s" (fmtArr l4)

    // --- unfold/mode_product rank-3 worked example (corpus math/03x):
    // X: 3x4x2, X(i,j,k) = 1 + i + 3*j + 12*k  (Kolda–Bader's example
    // tensor in 0-based form: mode-0 unfolding columns count 1..24) ---
    let dims3 = [3; 4; 2]
    let x3 =
        { Tensor.Dims = dims3
          Tensor.Data =
            [| for i in 0 .. 2 do
                 for j in 0 .. 3 do
                   for k in 0 .. 1 -> float (1 + i + 3 * j + 12 * k) |] }
    for mode in 0 .. 2 do
        let m = Tensor.unfold x3 mode
        printfn "unfold 3x4x2 mode %d = %s" mode (fmtArr (flat2 m))
    let u23 = array2D [ [1.0; 0.5; 0.25]; [2.0; 1.0; 0.0] ]
    let y = Tensor.modeProduct x3 u23 0
    printfn "mode_product 3x4x2 x U(2x3) mode 0: dims = %A, data = %s" y.Dims (fmtArr y.Data)

    // --- eig: triangular 3x3 (corpus math/050) — eigenvalues = diagonal ---
    let tr3 = array2D [ [3.0; 1.0; 2.0]; [0.0; 1.0; 5.0]; [0.0; 0.0; 2.0] ]
    let (re1, im1) = Eig.eig 90 tr3
    printfn "eig triangular 3x3: RE = %s IM = %s (expect 3,2,1 / zeros)" (fmtArr re1) (fmtArr im1)

    // --- eig: rotation 2x2 theta=0.3 (corpus math/051) — cos ± i sin ---
    let th = 0.3
    let rot = array2D [ [cos th; -(sin th)]; [sin th; cos th] ]
    let (re2, im2) = Eig.eig 60 rot
    printfn "eig rotation 2x2: RE = %s IM = %s" (fmtArr re2) (fmtArr im2)
    printfn "  analytic: cos = %s, sin = %s" (fmt (cos th)) (fmt (sin th))

    // --- eig: companion of (l-1)(l-2)(l-3) (corpus math/052) — real chase ---
    let comp3 = array2D [ [0.0; 1.0; 0.0]; [0.0; 0.0; 1.0]; [6.0; -11.0; 6.0] ]
    let (re3, im3) = Eig.eig 90 comp3
    printfn "eig companion real roots: RE = %s IM = %s (expect 3,2,1)" (fmtArr re3) (fmtArr im3)

    // --- eig: companion of (l-0.5)(l^2-l+0.625) (corpus math/053) — complex chase ---
    let compC = array2D [ [0.0; 1.0; 0.0]; [0.0; 0.0; 1.0]; [0.3125; -1.125; 1.5] ]
    let (re4, im4) = Eig.eig 90 compC
    printfn "eig companion complex pair: RE = %s IM = %s" (fmtArr re4) (fmtArr im4)
    printfn "  analytic: 0.5 ± %s i (mod %s), then 0.5" (fmt (sqrt 0.375)) (fmt (sqrt 0.625))

    // --- eig: coupled damped-rotation ⊕ decay (corpus math/054, Koopman-shaped):
    // A = S D S^{-1}, D = rot(0.9, 0.7) ⊕ diag(0.7, 0.4), S unit lower
    // bidiagonal — spectrum 0.9e^{±0.7i}, 0.7, 0.4 with full coupling ---
    let r = 0.9
    let w = 0.7
    let dm = array2D [ [r * cos w; -(r * sin w); 0.0; 0.0]
                       [r * sin w; r * cos w; 0.0; 0.0]
                       [0.0; 0.0; 0.7; 0.0]
                       [0.0; 0.0; 0.0; 0.4] ]
    let sMat = array2D [ [1.0; 0.0; 0.0; 0.0]; [1.0; 1.0; 0.0; 0.0]; [0.0; 1.0; 1.0; 0.0]; [0.0; 0.0; 1.0; 1.0] ]
    let sInv = array2D [ [1.0; 0.0; 0.0; 0.0]; [-1.0; 1.0; 0.0; 0.0]; [1.0; -1.0; 1.0; 0.0]; [-1.0; 1.0; -1.0; 1.0] ]
    let mm (a: float[,]) (b: float[,]) =
        Array2D.init 4 4 (fun i j ->
            let mutable acc = 0.0
            for t in 0 .. 3 do acc <- acc + a.[i, t] * b.[t, j]
            acc)
    let koop = mm (mm sMat dm) sInv
    printfn "eig koopman 4x4 fixture A = %s" (fmtArr (flat2 koop))
    let (re5, im5) = Eig.eig 120 koop
    printfn "eig koopman 4x4: RE = %s IM = %s" (fmtArr re5) (fmtArr im5)
    printfn "  analytic: %s ± %s i (mod 0.9), 0.7, 0.4" (fmt (r * cos w)) (fmt (r * sin w))

    // --- eig vs eigh cross-check on the symmetric 4x4 fixture ---
    let (re6, im6) = Eig.eig 120 s4
    let (_, l4b) = Jacobi.eigh 10 s4
    let maxd = Array.map2 (fun a b -> abs (a - b)) re6 l4b |> Array.max
    let maxim = im6 |> Array.map abs |> Array.max
    printfn "eig vs eigh symmetric 4x4: RE = %s (max |diff| = %s, max |im| = %s)" (fmtArr re6) (fmt maxd) (fmt maxim)

    // --- hosvd rank-3 fixture (corpus math/04x): 3x3x3 ---
    let dims33 = [3; 3; 3]
    let x33 =
        { Tensor.Dims = dims33
          Tensor.Data =
            [| for i in 0 .. 2 do
                 for j in 0 .. 2 do
                   for k in 0 .. 2 ->
                     float ((i + 1) * (j + 2)) + float (k * k) * 0.5 + float (i * j * k) * 0.25 |] }
    let (core, factors) = Tensor.hosvd 10 x33 dims33
    printfn "hosvd 3x3x3 fixture:"
    printfn "  core = %s" (fmtArr core.Data)
    factors |> List.iteri (fun i f -> printfn "  U%d = %s" (i + 1) (fmtArr (flat2 f)))
    // core column norms per mode (robust pins)
    let coreT = { Tensor.Dims = dims33; Tensor.Data = core.Data }
    for mode in 0 .. 2 do
        let m = Tensor.unfold coreT mode
        let norms = [| for r in 0 .. 2 -> sqrt (Array.sum [| for c in 0 .. Array2D.length2 m - 1 -> m.[r, c] * m.[r, c] |]) |]
        printfn "  core mode-%d slice norms = %s" mode (fmtArr norms)
