/// Reference tensor ops for the math module: Kolda–Bader mode-n
/// matricization, mode products, and HOSVD over flat row-major storage.
/// The VALUE ORACLE for the rank-generic generated kernels (phases 3–4).
module BladeMath.Tensor

/// A dense tensor: dims (outermost first) + flat row-major data.
type Tensor = { Dims: int list; Data: float[] }

let private prod xs = List.fold (*) 1 xs

/// Row-major strides: stride_k = Π_{m>k} I_m.
let strides (dims: int list) : int list =
    let n = dims.Length
    [ for k in 0 .. n - 1 -> prod (List.skip (k + 1) dims) ]

/// Kolda–Bader column index weights for mode-n unfolding, 0-based:
/// J_k = Π_{m<k, m≠mode} I_m  (k ≠ mode).
let unfoldWeights (dims: int list) (mode: int) : int list =
    [ for k in 0 .. dims.Length - 1 ->
        if k = mode then 0
        else prod [ for m in 0 .. k - 1 do if m <> mode then yield dims.[m] ] ]

/// All multi-indices of a dims-shaped tensor, row-major order.
let multiIndices (dims: int list) : int list seq =
    let rec go ds =
        seq {
            match ds with
            | [] -> yield []
            | d :: rest ->
                for i in 0 .. d - 1 do
                    for tail in go rest -> i :: tail
        }
    go dims

/// Mode-n unfolding: M(i_mode, j), j = Σ_{k≠mode} i_k · J_k.
let unfold (t: Tensor) (mode: int) : float[,] =
    let dims = t.Dims
    let str = strides dims
    let jw = unfoldWeights dims mode
    let rows = dims.[mode]
    let cols = prod dims / rows
    let m = Array2D.zeroCreate rows cols
    for ix in multiIndices dims do
        let flat = List.map2 (*) ix str |> List.sum
        let j = List.mapi (fun k i -> if k = mode then 0 else i * jw.[k]) ix |> List.sum
        m.[ix.[mode], j] <- t.Data.[flat]
    m

/// Mode-n product Y = X ×_mode U with U: j×I_mode —
/// Y(..., r, ...) = Σ_i U(r, i) · X(..., i, ...).
let modeProduct (t: Tensor) (u: float[,]) (mode: int) : Tensor =
    let dims = t.Dims
    let iMode = dims.[mode]
    let j = Array2D.length1 u
    if Array2D.length2 u <> iMode then failwith "modeProduct: U columns must match the mode extent"
    let outDims = dims |> List.mapi (fun k d -> if k = mode then j else d)
    let strIn = strides dims
    let strOut = strides outDims
    let data = Array.zeroCreate (prod outDims)
    for ox in multiIndices outDims do
        let mutable acc = 0.0
        for i in 0 .. iMode - 1 do
            let ix = ox |> List.mapi (fun k o -> if k = mode then i else o)
            let flatIn = List.map2 (*) ix strIn |> List.sum
            acc <- acc + u.[ox.[mode], i] * t.Data.[flatIn]
        let flatOut = List.map2 (*) ox strOut |> List.sum
        data.[flatOut] <- acc
    { Dims = outDims; Data = data }

/// Mode-n Gram: G(a,b) = Σ_{other indices} X(..,a,..)·X(..,b,..)
/// (= X_(n) · X_(n)ᵀ without materializing the unfolding).
let modeGram (t: Tensor) (mode: int) : float[,] =
    let m = unfold t mode
    let rows = Array2D.length1 m
    let cols = Array2D.length2 m
    let g = Array2D.zeroCreate rows rows
    for a in 0 .. rows - 1 do
        for b in 0 .. rows - 1 do
            let mutable acc = 0.0
            for c in 0 .. cols - 1 do acc <- acc + m.[a, c] * m.[b, c]
            g.[a, b] <- acc
    g

/// Leading r columns of a matrix.
let leadingCols (r: int) (q: float[,]) : float[,] =
    Array2D.init (Array2D.length1 q) r (fun i j -> q.[i, j])

/// (Truncated) HOSVD: per mode, U_n = leading R_n eigenvectors of the
/// mode-n Gram (descending eigenvalues); core = X ×_1 U1ᵀ ... ×_N UNᵀ.
/// Returns (core, factors). ranks = dims for the full HOSVD.
let hosvd (sweeps: int) (t: Tensor) (ranks: int list) : Tensor * float[,] list =
    let factors =
        t.Dims |> List.mapi (fun mode _ ->
            let g = modeGram t mode
            let (q, _) = Jacobi.eigh sweeps g
            leadingCols ranks.[mode] q)
    // core: contract each mode with Uᵀ (i.e. modeProduct with U transposed)
    let core =
        factors |> List.mapi (fun mode u -> (mode, u))
        |> List.fold (fun acc (mode, u) ->
            let ut = Array2D.init (Array2D.length2 u) (Array2D.length1 u) (fun i j -> u.[j, i])
            modeProduct acc ut mode) t
    (core, factors)
