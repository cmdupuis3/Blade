/// Math-module decl builders: dense linear-algebra and tensor-decomposition
/// kernels (matmul, one-sided Jacobi SVD, cyclic Jacobi eigh, mode-n
/// unfolding, mode products, HOSVD) synthesized as plain Blade source.
///
/// Rank polymorphism lives HERE: each builder is ONE order-generic F#
/// function that emits a fixed-rank Blade FunctionDecl per requested tensor
/// order (the PplElaborate moment-tower pattern — F# loops over the order,
/// the generated code is straight per-rank nests).
///
/// House style for generated bodies (the LDL lesson from
/// examples/physics/41: long scalar let-chains inline exponentially in
/// codegen — never emit them):
///   - flat rank-1 `mut` work arrays with static-literal strides,
///   - for-in loop nests (runtime-expression bounds allowed),
///   - if-EXPRESSIONS for guards (no statement-level control flow),
///   - rank-2+ inputs copied in via `w(i*n+j) = a(i, j)`,
///   - rank-2+ outputs assembled as nested array literals of runtime reads.
///
/// Unfolding convention (documented once, used everywhere): Kolda–Bader
/// mode-n matricization, 0-based — X(i_0..i_{N-1}) maps to M(i_mode, j)
/// with j = Σ_{k≠mode} i_k · J_k, J_k = Π_{m<k, m≠mode} I_m.
module Blade.Math.Decls

open Blade.Ast

// ============================================================================
// AST construction helpers (mirroring MLElaborate's style)
// ============================================================================

let v (n: string) = syn (ExprVar n)
let fLit (x: float) = syn (ExprLit (LitFloat x))
let iLit (n: int) = syn (ExprLit (LitInt (int64 n)))
let add a b = syn (ExprBinOp (Elementwise, OpAdd, a, b))
let sub a b = syn (ExprBinOp (Elementwise, OpSub, a, b))
let mul a b = syn (ExprBinOp (Elementwise, OpMul, a, b))
let divE a b = syn (ExprBinOp (Elementwise, OpDiv, a, b))
let idx (arr: string) (i: Expr) = syn (ExprApp (v arr, [i]))
let sLet n value = StmtLet { Pattern = synPat (PatVar n); Type = None; Value = value; Mutability = BindLet }
let sLetMut n value = StmtLet { Pattern = synPat (PatVar n); Type = None; Value = value; Mutability = BindMut }
let sAccum lhs e = StmtExpr (syn (ExprAssign (lhs, add lhs e)))
let sAssign lhs e = StmtExpr (syn (ExprAssign (lhs, e)))
let sFor var lo hi body = StmtForIn (var, syn (ExprDotDot (iLit lo, iLit hi)), body)
/// for-in with expression bounds (runtime lower bounds are proven: ml's
/// tpDecl iterates table-driven ranges).
let sForE var loE hiE body = StmtForIn (var, syn (ExprDotDot (loE, hiE)), body)
let zerosLit (n: int) = syn (ExprArrayLit (List.replicate n (fLit 0.0)))
let tyFloat = TyNamed ("Float", [])
let tyFloatArr (n: int) = TyArray (tyFloat, [ TyIdx (iLit n) ])

let mkFunc name (ps: (string * TypeExpr) list) retTy body : FunctionDecl =
    { Name = name
      TypeParams = []
      Params = ps |> List.map (fun (n, t) -> { Name = n; Type = Some t; Mutability = Immutable })
      WhereClause = None
      ReturnType = Some retTy
      Body = body
      IsStatic = false }

let absE e = syn (ExprApp (v "abs", [e]))
let sqrtE e = syn (ExprApp (v "sqrt", [e]))
let negOne = syn (ExprUnaryOp (OpNeg, fLit 1.0))
let cmp op a b = syn (ExprBinOp (Elementwise, op, a, b))
/// a(i, j) — multi-subscript read (runtime indices proven, func-arrays/010).
let idx2 (arr: string) (i: Expr) (j: Expr) = syn (ExprApp (v arr, [i; j]))
/// Flat row-major cell arr(i*stride + j) of a rank-1 work array.
let flat (arr: string) (stride: int) (i: Expr) (j: Expr) =
    idx arr (add (mul i (iLit stride)) j)
/// Rank-2 nested literal of runtime reads over a flat work array
/// ([[c(0), c(1)], [c(2), c(3)]] — proven by corpus math/005).
let nestedFromFlat (arr: string) (rows: int) (cols: int) : Expr =
    syn (ExprArrayLit
        [ for i in 0 .. rows - 1 ->
            syn (ExprArrayLit [ for j in 0 .. cols - 1 -> idx arr (iLit (i * cols + j)) ]) ])
let tyFloatMat (rows: int) (cols: int) =
    TyArray (tyFloat, [ TyIdx (iLit rows); TyIdx (iLit cols) ])

/// Default Jacobi sweep budget: cyclic Jacobi converges quadratically once
/// sweeps begin; 6–8 reach machine epsilon at n <= ~100, 10 gives margin.
/// Converged rotations guard to the identity, so surplus sweeps are cheap.
let defaultSweeps = 10

let prodInts (xs: int list) = List.fold (*) 1 xs
/// Rank-N dense tensor type: Array<Float like Idx<d0>, Idx<d1>, ...>.
let tyFloatTensor (dims: int list) =
    TyArray (tyFloat, dims |> List.map (fun d -> TyIdx (iLit d)))
/// Rank-N nested literal of runtime reads over a flat row-major work array
/// (the rank-N generalization of nestedFromFlat).
let rec nestedFromFlatN (arr: string) (dims: int list) (offset: int) : Expr =
    match dims with
    | [] -> failwith "nestedFromFlatN: empty dims"
    | [last] -> syn (ExprArrayLit [ for i in 0 .. last - 1 -> idx arr (iLit (offset + i)) ])
    | d :: rest ->
        let stride = prodInts rest
        syn (ExprArrayLit [ for i in 0 .. d - 1 -> nestedFromFlatN arr rest (offset + i * stride) ])
/// N-deep loop nest: vars/extents outermost first, around the given body.
let loopNest (vars: string list) (extents: int list) (body: Stmt list) : Stmt list =
    List.foldBack2 (fun name extent acc -> [ sFor name 0 extent acc ]) vars extents body

// Span-stamping wrappers for the raw combinators used in decl bodies (full-
// span AST): each attributes to the ambient synthSpan the elaborator stamps.
let ifE (c, t, f) = syn (ExprIf (c, t, f))
let blockE (stmts, fin) = syn (ExprBlock (stmts, fin))
let tupleE (xs: Expr list) = syn (ExprTuple xs)

// ============================================================================
// matmul
// ============================================================================

/// matmul for fixed (m, k, n): C(i,j) = Σ_t A(i,t)·B(t,j). Flat mut
/// accumulator + triple nest; the result is a nested literal of reads.
let matmulDecl (name: string) (m: int) (k: int) (n: int) : FunctionDecl =
    let body =
        blockE (
            [ sLetMut "c" (zerosLit (m * n))
              sFor "i" 0 m
                [ sFor "j" 0 n
                    [ sFor "t" 0 k
                        [ sAccum (flat "c" n (v "i") (v "j"))
                                 (mul (idx2 "a" (v "i") (v "t")) (idx2 "b" (v "t") (v "j"))) ] ] ] ],
            Some (nestedFromFlat "c" m n))
    mkFunc name [ ("a", tyFloatMat m k); ("b", tyFloatMat k n) ] (tyFloatMat m n) body

// ============================================================================
// svd (one-sided / Hestenes cyclic Jacobi)
// ============================================================================

/// Thin SVD for fixed (m, n, sweeps), m >= n: A ≈ U·diag(S)·Vᵀ with S
/// descending, U m×n (columns orthonormal; zero σ -> zero column), V n×n
/// (COLUMNS are the right singular vectors: A(i,j) = Σ_k U(i,k)·S(k)·V(j,k)).
///
/// One-sided Jacobi on a working copy W: for each column pair (p, q) the
/// rotation angle comes from the 2×2 Gram entries a=‖wp‖², b=‖wq‖²,
/// c=wp·wq via the stable smaller-root formula (t² + 2ζt − 1 = 0,
/// ζ=(b−a)/2c); a pair with |c| <= 1e-15·√(a·b) guards to the identity
/// through if-EXPRESSIONS (no statement control flow). V accumulates the
/// same column rotations from an identity. Post: S = column norms,
/// selection-sort descending with column swaps, U = normalized columns,
/// sign fix (first row attaining max |entry| per U column made positive;
/// U and V columns flip together).
///
/// Scratch muts are hoisted to the top and re-zeroed per use (no
/// `let mut` inside loop bodies).
let svdDecl (name: string) (m: int) (n: int) (sweeps: int) : FunctionDecl =
    let wAt = flat "w" n
    let vAt = flat "vv" n
    let uAt = flat "u" n
    let body =
        blockE (
            [ // working copy of a (row-major flat, stride n)
              sLetMut "w" (zerosLit (m * n))
              sFor "i" 0 m
                [ sFor "j" 0 n [ sAssign (wAt (v "i") (v "j")) (idx2 "a" (v "i") (v "j")) ] ]
              // V accumulator = identity
              sLetMut "vv" (zerosLit (n * n))
              sFor "i" 0 n [ sAssign (vAt (v "i") (v "i")) (fLit 1.0) ]
              // hoisted scratch
              sLetMut "aa" (fLit 0.0)
              sLetMut "bb" (fLit 0.0)
              sLetMut "cc" (fLit 0.0)
              sLetMut "best" (iLit 0)
              sLetMut "bigv" (fLit 0.0)
              // cyclic one-sided Jacobi sweeps
              sFor "sweep" 0 sweeps
                [ sFor "p" 0 (n - 1)
                    [ sForE "q" (add (v "p") (iLit 1)) (iLit n)
                        [ sAssign (v "aa") (fLit 0.0)
                          sAssign (v "bb") (fLit 0.0)
                          sAssign (v "cc") (fLit 0.0)
                          sFor "i" 0 m
                            [ sLet "wp" (wAt (v "i") (v "p"))
                              sLet "wq" (wAt (v "i") (v "q"))
                              sAccum (v "aa") (mul (v "wp") (v "wp"))
                              sAccum (v "bb") (mul (v "wq") (v "wq"))
                              sAccum (v "cc") (mul (v "wp") (v "wq")) ]
                          sLet "conv" (cmp OpLe (absE (v "cc"))
                                                (mul (fLit 1.0e-15) (sqrtE (mul (v "aa") (v "bb")))))
                          sLet "zeta" (divE (sub (v "bb") (v "aa"))
                                            (ifE (v "conv", fLit 1.0, mul (fLit 2.0) (v "cc"))))
                          sLet "tt" (divE (ifE (cmp OpGe (v "zeta") (fLit 0.0), fLit 1.0, negOne))
                                          (add (absE (v "zeta"))
                                               (sqrtE (add (fLit 1.0) (mul (v "zeta") (v "zeta"))))))
                          sLet "cs" (ifE (v "conv", fLit 1.0,
                                             divE (fLit 1.0) (sqrtE (add (fLit 1.0) (mul (v "tt") (v "tt"))))))
                          sLet "sn" (ifE (v "conv", fLit 0.0, mul (v "cs") (v "tt")))
                          // rotate columns p, q of w (scalar-buffered, no aliasing)
                          sFor "i" 0 m
                            [ sLet "tp" (wAt (v "i") (v "p"))
                              sLet "tq" (wAt (v "i") (v "q"))
                              sAssign (wAt (v "i") (v "p")) (sub (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                              sAssign (wAt (v "i") (v "q")) (add (mul (v "sn") (v "tp")) (mul (v "cs") (v "tq"))) ]
                          // accumulate the same rotation into vv
                          sFor "i" 0 n
                            [ sLet "tp" (vAt (v "i") (v "p"))
                              sLet "tq" (vAt (v "i") (v "q"))
                              sAssign (vAt (v "i") (v "p")) (sub (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                              sAssign (vAt (v "i") (v "q")) (add (mul (v "sn") (v "tp")) (mul (v "cs") (v "tq"))) ] ] ] ]
              // singular values = column norms of w
              sLetMut "s" (zerosLit n)
              sFor "j" 0 n
                [ sAssign (v "aa") (fLit 0.0)
                  sFor "i" 0 m
                    [ sLet "wj" (wAt (v "i") (v "j"))
                      sAccum (v "aa") (mul (v "wj") (v "wj")) ]
                  sAssign (idx "s" (v "j")) (sqrtE (v "aa")) ]
              // selection sort descending (ties keep original order); swap
              // s entries and the matching columns of w and vv
              sFor "kk" 0 n
                [ sAssign (v "best") (v "kk")
                  sForE "j" (add (v "kk") (iLit 1)) (iLit n)
                    [ sAssign (v "best") (ifE (cmp OpGt (idx "s" (v "j")) (idx "s" (v "best")), v "j", v "best")) ]
                  sLet "ts" (idx "s" (v "kk"))
                  sAssign (idx "s" (v "kk")) (idx "s" (v "best"))
                  sAssign (idx "s" (v "best")) (v "ts")
                  sFor "i" 0 m
                    [ sLet "tw" (wAt (v "i") (v "kk"))
                      sAssign (wAt (v "i") (v "kk")) (wAt (v "i") (v "best"))
                      sAssign (wAt (v "i") (v "best")) (v "tw") ]
                  sFor "i" 0 n
                    [ sLet "tv" (vAt (v "i") (v "kk"))
                      sAssign (vAt (v "i") (v "kk")) (vAt (v "i") (v "best"))
                      sAssign (vAt (v "i") (v "best")) (v "tv") ] ]
              // U = normalized columns (zero σ -> zero column, documented)
              sLetMut "u" (zerosLit (m * n))
              sFor "j" 0 n
                [ sLet "inv" (ifE (cmp OpGt (idx "s" (v "j")) (fLit 1.0e-300),
                                      divE (fLit 1.0) (idx "s" (v "j")), fLit 0.0))
                  sFor "i" 0 m [ sAssign (uAt (v "i") (v "j")) (mul (wAt (v "i") (v "j")) (v "inv")) ] ]
              // sign fix: first row attaining max |entry| per U column made
              // positive; U and V columns flip together (best reads the OLD
              // bigv before bigv updates — first-max semantics)
              sFor "j" 0 n
                [ sAssign (v "bigv") (fLit 0.0)
                  sAssign (v "best") (iLit 0)
                  sFor "i" 0 m
                    [ sLet "mag" (absE (uAt (v "i") (v "j")))
                      sAssign (v "best") (ifE (cmp OpGt (v "mag") (v "bigv"), v "i", v "best"))
                      sAssign (v "bigv") (ifE (cmp OpGt (v "mag") (v "bigv"), v "mag", v "bigv")) ]
                  sLet "flip" (ifE (cmp OpLt (uAt (v "best") (v "j")) (fLit 0.0), negOne, fLit 1.0))
                  sFor "i" 0 m [ sAssign (uAt (v "i") (v "j")) (mul (uAt (v "i") (v "j")) (v "flip")) ]
                  sFor "i" 0 n [ sAssign (vAt (v "i") (v "j")) (mul (vAt (v "i") (v "j")) (v "flip")) ] ]
              // assemble rank-2 outputs as named lets: array literals are
              // NOT supported directly inside a tuple construction (codegen
              // IRArrayLit gap) — tuple-of-variables is the proven boundary
              sLet "uo" (nestedFromFlat "u" m n)
              sLet "vo" (nestedFromFlat "vv" n n) ],
            Some (tupleE [ v "uo"; v "s"; v "vo" ]))
    mkFunc name [ ("a", tyFloatMat m n) ]
        (TyTuple [ tyFloatMat m n; tyFloatArr n; tyFloatMat n n ]) body

// ============================================================================
// eigh (cyclic two-sided Jacobi, symmetric input assumed)
// ============================================================================

/// Symmetric eigendecomposition for fixed (n, sweeps): S ≈ Q·diag(LAM)·Qᵀ
/// with LAM descending and Q's COLUMNS the eigenvectors. Symmetry of the
/// input is ASSUMED, not checked (v1).
///
/// Cyclic two-sided Jacobi on a working copy AW: for pivot (p, q) the
/// angle comes from θ = (a_qq − a_pp)/(2·a_pq) via the same stable
/// smaller-root formula as svd (t² + 2θt − 1 = 0); |a_pq| <=
/// 1e-15·√(|a_pp·a_qq| + 1e-300) guards to the identity (the tiny absolute
/// floor keeps the guard alive on indefinite/zero diagonals). Each
/// rotation updates AW's columns AND rows (AW ← RᵀAWR) and accumulates the
/// column rotation into QM. Post: eigenvalues = diagonal, selection-sort
/// descending with QM column swaps, sign fix on QM columns (first row
/// attaining max |entry| made positive). Mirrors math/Jacobi.fs `eigh`
/// operation-for-operation, so values agree to the ulp.
let eighDecl (name: string) (n: int) (sweeps: int) : FunctionDecl =
    let aAt = flat "aw" n
    let qAt = flat "qm" n
    let body =
        blockE (
            [ // working copy of the symmetric input
              sLetMut "aw" (zerosLit (n * n))
              sFor "i" 0 n
                [ sFor "j" 0 n [ sAssign (aAt (v "i") (v "j")) (idx2 "a" (v "i") (v "j")) ] ]
              // eigenvector accumulator = identity
              sLetMut "qm" (zerosLit (n * n))
              sFor "i" 0 n [ sAssign (qAt (v "i") (v "i")) (fLit 1.0) ]
              // hoisted scratch
              sLetMut "best" (iLit 0)
              sLetMut "bigv" (fLit 0.0)
              // cyclic two-sided Jacobi sweeps
              sFor "sweep" 0 sweeps
                [ sFor "p" 0 (n - 1)
                    [ sForE "qq" (add (v "p") (iLit 1)) (iLit n)
                        [ sLet "apq" (aAt (v "p") (v "qq"))
                          sLet "app" (aAt (v "p") (v "p"))
                          sLet "aqq" (aAt (v "qq") (v "qq"))
                          sLet "conv" (cmp OpLe (absE (v "apq"))
                                                (mul (fLit 1.0e-15)
                                                     (sqrtE (add (absE (mul (v "app") (v "aqq"))) (fLit 1.0e-300)))))
                          sLet "theta" (divE (sub (v "aqq") (v "app"))
                                             (ifE (v "conv", fLit 1.0, mul (fLit 2.0) (v "apq"))))
                          sLet "tt" (divE (ifE (cmp OpGe (v "theta") (fLit 0.0), fLit 1.0, negOne))
                                          (add (absE (v "theta"))
                                               (sqrtE (add (fLit 1.0) (mul (v "theta") (v "theta"))))))
                          sLet "cs" (ifE (v "conv", fLit 1.0,
                                             divE (fLit 1.0) (sqrtE (add (fLit 1.0) (mul (v "tt") (v "tt"))))))
                          sLet "sn" (ifE (v "conv", fLit 0.0, mul (v "cs") (v "tt")))
                          // AW ← AW·R (columns p, qq), scalar-buffered
                          sFor "i" 0 n
                            [ sLet "tp" (aAt (v "i") (v "p"))
                              sLet "tq" (aAt (v "i") (v "qq"))
                              sAssign (aAt (v "i") (v "p")) (sub (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                              sAssign (aAt (v "i") (v "qq")) (add (mul (v "sn") (v "tp")) (mul (v "cs") (v "tq"))) ]
                          // AW ← Rᵀ·AW (rows p, qq)
                          sFor "i" 0 n
                            [ sLet "tp" (aAt (v "p") (v "i"))
                              sLet "tq" (aAt (v "qq") (v "i"))
                              sAssign (aAt (v "p") (v "i")) (sub (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                              sAssign (aAt (v "qq") (v "i")) (add (mul (v "sn") (v "tp")) (mul (v "cs") (v "tq"))) ]
                          // accumulate the column rotation into QM
                          sFor "i" 0 n
                            [ sLet "tp" (qAt (v "i") (v "p"))
                              sLet "tq" (qAt (v "i") (v "qq"))
                              sAssign (qAt (v "i") (v "p")) (sub (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                              sAssign (qAt (v "i") (v "qq")) (add (mul (v "sn") (v "tp")) (mul (v "cs") (v "tq"))) ] ] ] ]
              // eigenvalues = diagonal
              sLetMut "lam" (zerosLit n)
              sFor "j" 0 n [ sAssign (idx "lam" (v "j")) (aAt (v "j") (v "j")) ]
              // selection sort descending (ties keep original order) + QM
              // column swaps
              sFor "kk" 0 n
                [ sAssign (v "best") (v "kk")
                  sForE "j" (add (v "kk") (iLit 1)) (iLit n)
                    [ sAssign (v "best") (ifE (cmp OpGt (idx "lam" (v "j")) (idx "lam" (v "best")), v "j", v "best")) ]
                  sLet "tl" (idx "lam" (v "kk"))
                  sAssign (idx "lam" (v "kk")) (idx "lam" (v "best"))
                  sAssign (idx "lam" (v "best")) (v "tl")
                  sFor "i" 0 n
                    [ sLet "tq" (qAt (v "i") (v "kk"))
                      sAssign (qAt (v "i") (v "kk")) (qAt (v "i") (v "best"))
                      sAssign (qAt (v "i") (v "best")) (v "tq") ] ]
              // sign fix on QM columns
              sFor "j" 0 n
                [ sAssign (v "bigv") (fLit 0.0)
                  sAssign (v "best") (iLit 0)
                  sFor "i" 0 n
                    [ sLet "mag" (absE (qAt (v "i") (v "j")))
                      sAssign (v "best") (ifE (cmp OpGt (v "mag") (v "bigv"), v "i", v "best"))
                      sAssign (v "bigv") (ifE (cmp OpGt (v "mag") (v "bigv"), v "mag", v "bigv")) ]
                  sLet "flip" (ifE (cmp OpLt (qAt (v "best") (v "j")) (fLit 0.0), negOne, fLit 1.0))
                  sFor "i" 0 n [ sAssign (qAt (v "i") (v "j")) (mul (qAt (v "i") (v "j")) (v "flip")) ] ]
              // tuple-of-variables boundary (IRArrayLit-in-tuple gap)
              sLet "qo" (nestedFromFlat "qm" n n) ],
            Some (tupleE [ v "qo"; v "lam" ]))
    mkFunc name [ ("a", tyFloatMat n n) ]
        (TyTuple [ tyFloatMat n n; tyFloatArr n ]) body

// ============================================================================
// eig (general non-symmetric: Hessenberg + Francis double-shift QR)
// ============================================================================

/// General real eigenvalues for a fixed (n, maxIter): (LRE, LIM) sorted by
/// DESCENDING modulus, conjugate pairs adjacent with +im first. Mirrors
/// math/Eig.fs operation-for-operation (Householder Hessenberg, windowed
/// Francis double-shift QR with a fixed budget, quasi-triangular
/// extraction), so values agree with the oracle to the ulp.
///
/// Control flow discipline (no statement-level branching in Blade):
/// conditional work runs as EMPTY loop ranges (runtime bounds), IDENTITY
/// rotations (cs=1, sn=0), zero Householder betas, and self-assign guards;
/// composite conditions are Int 0/1 flags composed by multiplication
/// (OpAnd/OpOr never reach codegen). Indices that would go out of range in
/// the guarded-off case are CLAMPED to safe in-bounds dummies (lc/ec) whose
/// writes are identity ops; n <= 2 emits no Hessenberg/QR code at all (the
/// extraction's exact 1×1/2×2 solve covers it).
///
/// No exceptional shifts (documented): adversarial cyclic-permutation
/// spectra (exact equal-modulus roots of unity) can stall within the
/// budget; generic data-derived matrices (Koopman/EDMD) converge normally.
let eigDecl (name: string) (n: int) (maxIter: int) : FunctionDecl =
    let hAt = flat "hw" n
    let bint c = ifE (c, iLit 1, iLit 0)
    let isOne e = cmp OpEq e (iLit 1)
    let one_ = iLit 1
    let epsE = fLit 1.0e-12
    let tinyE = fLit 1.0e-300
    let kp1 = add (v "k") one_
    let kp2 = add (v "k") (iLit 2)
    let kp3 = add (v "k") (iLit 3)
    let km1 = sub (v "k") one_
    let ecm1 = sub (v "ec") one_
    let copyIn =
        [ sLetMut "hw" (zerosLit (n * n))
          sFor "i" 0 n [ sFor "j" 0 n [ sAssign (hAt (v "i") (v "j")) (idx2 "a" (v "i") (v "j")) ] ] ]
    let scratch =
        [ sLetMut "hv" (zerosLit n)
          sLetMut "acc" (fLit 0.0)
          sLetMut "sm" (fLit 0.0)
          sLetMut "hi" (iLit n)
          sLetMut "l" (iLit 0)
          sLetMut "x" (fLit 0.0)
          sLetMut "y" (fLit 0.0)
          sLetMut "z" (fLit 0.0)
          sLetMut "skip" (iLit 0)
          sLetMut "best" (iLit 0) ]
    // ---- Householder Hessenberg (emitted only for n >= 3) ----
    let hess =
        if n < 3 then [] else
        [ sFor "k" 0 (n - 2)
            [ sAssign (v "acc") (fLit 0.0)
              sForE "i" kp1 (iLit n)
                [ sLet "hik" (hAt (v "i") (v "k"))
                  sAccum (v "acc") (mul (v "hik") (v "hik")) ]
              sLet "nrm" (sqrtE (v "acc"))
              sLet "fok" (bint (cmp OpGt (v "nrm") tinyE))
              sLet "alpha" (ifE (cmp OpGe (hAt kp1 (v "k")) (fLit 0.0),
                                    syn (ExprUnaryOp (OpNeg, v "nrm")), v "nrm"))
              sFor "i" 0 n [ sAssign (idx "hv" (v "i")) (fLit 0.0) ]
              sAssign (idx "hv" kp1) (sub (hAt kp1 (v "k")) (v "alpha"))
              sForE "i" kp2 (iLit n) [ sAssign (idx "hv" (v "i")) (hAt (v "i") (v "k")) ]
              sAssign (v "acc") (fLit 0.0)
              sForE "i" kp1 (iLit n)
                [ sLet "hvi" (idx "hv" (v "i"))
                  sAccum (v "acc") (mul (v "hvi") (v "hvi")) ]
              sLet "beta" (ifE (isOne (mul (v "fok") (bint (cmp OpGt (v "acc") tinyE))),
                                   divE (fLit 2.0) (v "acc"), fLit 0.0))
              // H <- (I - beta v vT) H
              sFor "j" 0 n
                [ sAssign (v "sm") (fLit 0.0)
                  sForE "i" kp1 (iLit n) [ sAccum (v "sm") (mul (idx "hv" (v "i")) (hAt (v "i") (v "j"))) ]
                  sForE "i" kp1 (iLit n)
                    [ sAssign (hAt (v "i") (v "j"))
                              (sub (hAt (v "i") (v "j")) (mul (mul (v "beta") (v "sm")) (idx "hv" (v "i")))) ] ]
              // H <- H (I - beta v vT)
              sFor "i" 0 n
                [ sAssign (v "sm") (fLit 0.0)
                  sForE "j" kp1 (iLit n) [ sAccum (v "sm") (mul (idx "hv" (v "j")) (hAt (v "i") (v "j"))) ]
                  sForE "j" kp1 (iLit n)
                    [ sAssign (hAt (v "i") (v "j"))
                              (sub (hAt (v "i") (v "j")) (mul (mul (v "beta") (v "sm")) (idx "hv" (v "j")))) ] ]
              // exact zeros below the subdiagonal in column k
              sAssign (hAt kp1 (v "k")) (ifE (isOne (v "fok"), v "alpha", hAt kp1 (v "k")))
              sForE "i" kp2 (iLit n)
                [ sAssign (hAt (v "i") (v "k")) (ifE (isOne (v "fok"), fLit 0.0, hAt (v "i") (v "k"))) ] ] ]
    // ---- windowed Francis double-shift QR (emitted only for n >= 3) ----
    let francis =
        if n < 3 then [] else
        [ sFor "it" 0 maxIter
            [ // window start l = largest small subdiagonal below hi
              sAssign (v "l") (iLit 0)
              sForE "k" one_ (v "hi")
                [ sLet "sd" (absE (hAt (v "k") km1))
                  sLet "thr" (add (mul epsE (add (absE (hAt km1 km1)) (absE (hAt (v "k") (v "k"))))) tinyE)
                  sAssign (v "l") (ifE (cmp OpLe (v "sd") (v "thr"), v "k", v "l")) ]
              sLet "fact" (bint (cmp OpGe (v "hi") (iLit 2)))
              sLet "fchase" (mul (v "fact") (bint (cmp OpGt (sub (v "hi") (v "l")) (iLit 2))))
              // clamped window bounds: in-bounds dummies when not chasing
              sLet "lc" (ifE (isOne (v "fchase"), v "l", iLit 0))
              sLet "ec" (ifE (isOne (v "fchase"), sub (v "hi") one_, iLit 2))
              // double shift from the trailing 2x2 of the window
              sLet "sv" (add (hAt ecm1 ecm1) (hAt (v "ec") (v "ec")))
              sLet "tv" (sub (mul (hAt ecm1 ecm1) (hAt (v "ec") (v "ec")))
                             (mul (hAt ecm1 (v "ec")) (hAt (v "ec") ecm1)))
              // first column of (H - aI)(H - bI) e1
              sAssign (v "x") (add (sub (add (mul (hAt (v "lc") (v "lc")) (hAt (v "lc") (v "lc")))
                                             (mul (hAt (v "lc") (add (v "lc") one_)) (hAt (add (v "lc") one_) (v "lc"))))
                                        (mul (v "sv") (hAt (v "lc") (v "lc"))))
                                   (v "tv"))
              sAssign (v "y") (mul (hAt (add (v "lc") one_) (v "lc"))
                                   (sub (add (hAt (v "lc") (v "lc")) (hAt (add (v "lc") one_) (add (v "lc") one_))) (v "sv")))
              sAssign (v "z") (mul (hAt (add (v "lc") (iLit 2)) (add (v "lc") one_)) (hAt (add (v "lc") one_) (v "lc")))
              // bulge chase (empty range when not chasing)
              sForE "k" (v "lc") (ifE (isOne (v "fchase"), sub (v "ec") one_, v "lc"))
                [ sLet "cn" (sqrtE (add (add (mul (v "x") (v "x")) (mul (v "y") (v "y"))) (mul (v "z") (v "z"))))
                  sLet "okc" (bint (cmp OpGt (v "cn") tinyE))
                  sLet "calpha" (ifE (cmp OpGe (v "x") (fLit 0.0), syn (ExprUnaryOp (OpNeg, v "cn")), v "cn"))
                  sLet "w0" (sub (v "x") (v "calpha"))
                  sLet "w1" (v "y")
                  sLet "w2" (v "z")
                  sLet "cvv" (add (add (mul (v "w0") (v "w0")) (mul (v "w1") (v "w1"))) (mul (v "w2") (v "w2")))
                  sLet "cbeta" (ifE (isOne (mul (v "okc") (bint (cmp OpGt (v "cvv") tinyE))),
                                        divE (fLit 2.0) (v "cvv"), fLit 0.0))
                  // rows k..k+2 from the left
                  sFor "j" 0 n
                    [ sAssign (v "sm") (add (add (mul (v "w0") (hAt (v "k") (v "j")))
                                                 (mul (v "w1") (hAt kp1 (v "j"))))
                                            (mul (v "w2") (hAt kp2 (v "j"))))
                      sAssign (hAt (v "k") (v "j")) (sub (hAt (v "k") (v "j")) (mul (mul (v "cbeta") (v "sm")) (v "w0")))
                      sAssign (hAt kp1 (v "j")) (sub (hAt kp1 (v "j")) (mul (mul (v "cbeta") (v "sm")) (v "w1")))
                      sAssign (hAt kp2 (v "j")) (sub (hAt kp2 (v "j")) (mul (mul (v "cbeta") (v "sm")) (v "w2"))) ]
                  // cols k..k+2 from the right
                  sFor "i" 0 n
                    [ sAssign (v "sm") (add (add (mul (v "w0") (hAt (v "i") (v "k")))
                                                 (mul (v "w1") (hAt (v "i") kp1)))
                                            (mul (v "w2") (hAt (v "i") kp2)))
                      sAssign (hAt (v "i") (v "k")) (sub (hAt (v "i") (v "k")) (mul (mul (v "cbeta") (v "sm")) (v "w0")))
                      sAssign (hAt (v "i") kp1) (sub (hAt (v "i") kp1) (mul (mul (v "cbeta") (v "sm")) (v "w1")))
                      sAssign (hAt (v "i") kp2) (sub (hAt (v "i") kp2) (mul (mul (v "cbeta") (v "sm")) (v "w2"))) ]
                  // restore exact Hessenberg zeros behind the bulge (k > lc;
                  // the k = 0 flat index (k+1)·n + (k−1) = n−1 is in-bounds,
                  // and the guard self-assigns there)
                  sLet "fclean" (mul (v "okc") (bint (cmp OpGt (v "k") (v "lc"))))
                  sAssign (hAt kp1 km1) (ifE (isOne (v "fclean"), fLit 0.0, hAt kp1 km1))
                  sAssign (hAt kp2 km1) (ifE (isOne (v "fclean"), fLit 0.0, hAt kp2 km1))
                  sAssign (v "x") (hAt kp1 (v "k"))
                  sAssign (v "y") (hAt kp2 (v "k"))
                  sAssign (v "z") (ifE (cmp OpLt (v "k") (sub (v "ec") (iLit 2)), hAt kp3 (v "k"), fLit 0.0)) ]
              // final Givens on (x, y) over rows/cols ec-1, ec (identity
              // rotation at clamped dummy indices when not chasing)
              sLet "gn" (sqrtE (add (mul (v "x") (v "x")) (mul (v "y") (v "y"))))
              sLet "okg" (mul (v "fchase") (bint (cmp OpGt (v "gn") tinyE)))
              sLet "cs" (ifE (isOne (v "okg"), divE (v "x") (v "gn"), fLit 1.0))
              sLet "sn" (ifE (isOne (v "okg"), divE (v "y") (v "gn"), fLit 0.0))
              sFor "j" 0 n
                [ sLet "tp" (hAt ecm1 (v "j"))
                  sLet "tq" (hAt (v "ec") (v "j"))
                  sAssign (hAt ecm1 (v "j")) (add (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                  sAssign (hAt (v "ec") (v "j")) (sub (mul (v "cs") (v "tq")) (mul (v "sn") (v "tp"))) ]
              sFor "i" 0 n
                [ sLet "tp" (hAt (v "i") ecm1)
                  sLet "tq" (hAt (v "i") (v "ec"))
                  sAssign (hAt (v "i") ecm1) (add (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                  sAssign (hAt (v "i") (v "ec")) (sub (mul (v "cs") (v "tq")) (mul (v "sn") (v "tp"))) ]
              sAssign (hAt (v "ec") (sub (v "ec") (iLit 2)))
                      (ifE (isOne (v "okg"), fLit 0.0, hAt (v "ec") (sub (v "ec") (iLit 2))))
              // freeze windows of size <= 2 (extraction solves them exactly)
              sLet "ffreeze" (mul (v "fact") (bint (cmp OpLe (sub (v "hi") (v "l")) (iLit 2))))
              sAssign (v "hi") (ifE (isOne (v "ffreeze"), v "l", v "hi")) ] ]
    // ---- extraction: 1x1 blocks real, 2x2 blocks via the quadratic ----
    let extract =
        [ sLetMut "lre" (zerosLit n)
          sLetMut "lim" (zerosLit n)
          sFor "k" 0 n
            [ sLet "fhand" (v "skip")
              sLet "flast" (bint (cmp OpEq (v "k") (iLit (n - 1))))
              sLet "sd" (ifE (isOne (v "flast"), fLit 0.0, absE (hAt kp1 (v "k"))))
              sLet "thr" (ifE (isOne (v "flast"), fLit 1.0,
                                  add (mul epsE (add (absE (hAt (v "k") (v "k"))) (absE (hAt kp1 kp1)))) tinyE))
              sLet "freal" (ifE (isOne (v "flast"), iLit 1, bint (cmp OpLe (v "sd") (v "thr"))))
              sLet "a2" (hAt (v "k") (v "k"))
              sLet "b2" (ifE (isOne (v "flast"), fLit 0.0, hAt (v "k") kp1))
              sLet "c2" (ifE (isOne (v "flast"), fLit 0.0, hAt kp1 (v "k")))
              sLet "d2" (ifE (isOne (v "flast"), fLit 0.0, hAt kp1 kp1))
              sLet "pp" (mul (fLit 0.5) (add (v "a2") (v "d2")))
              sLet "disc" (add (mul (mul (fLit 0.25) (sub (v "a2") (v "d2"))) (sub (v "a2") (v "d2")))
                               (mul (v "b2") (v "c2")))
              sLet "rt" (sqrtE (absE (v "disc")))
              sLet "fdpos" (bint (cmp OpGe (v "disc") (fLit 0.0)))
              sLet "fnothand" (sub one_ (v "fhand"))
              sLet "fpair" (mul (v "fnothand") (sub one_ (v "freal")))
              sAssign (idx "lre" (v "k"))
                      (ifE (isOne (v "fnothand"),
                               ifE (isOne (v "freal"), v "a2",
                                       ifE (isOne (v "fdpos"), add (v "pp") (v "rt"), v "pp")),
                               idx "lre" (v "k")))
              sAssign (idx "lim" (v "k"))
                      (ifE (isOne (mul (v "fpair") (sub one_ (v "fdpos"))), v "rt",
                               ifE (isOne (v "fnothand"), fLit 0.0, idx "lim" (v "k"))))
              // second of pair (safe self-index when not a pair)
              sLet "k2" (ifE (isOne (v "fpair"), kp1, v "k"))
              sAssign (idx "lre" (v "k2"))
                      (ifE (isOne (v "fpair"),
                               ifE (isOne (v "fdpos"), sub (v "pp") (v "rt"), v "pp"),
                               idx "lre" (v "k2")))
              sAssign (idx "lim" (v "k2"))
                      (ifE (isOne (v "fpair"),
                               ifE (isOne (v "fdpos"), fLit 0.0, syn (ExprUnaryOp (OpNeg, v "rt"))),
                               idx "lim" (v "k2")))
              sAssign (v "skip") (v "fpair") ]
          // selection sort by modulus^2 descending (pair swap keeps re/im
          // aligned; ties keep original order — conjugates stay adjacent)
          sFor "kk" 0 n
            [ sAssign (v "best") (v "kk")
              sForE "j" (add (v "kk") one_) (iLit n)
                [ sLet "kj" (add (mul (idx "lre" (v "j")) (idx "lre" (v "j"))) (mul (idx "lim" (v "j")) (idx "lim" (v "j"))))
                  sLet "kb" (add (mul (idx "lre" (v "best")) (idx "lre" (v "best"))) (mul (idx "lim" (v "best")) (idx "lim" (v "best"))))
                  sAssign (v "best") (ifE (cmp OpGt (v "kj") (v "kb"), v "j", v "best")) ]
              sLet "tr" (idx "lre" (v "kk"))
              sAssign (idx "lre" (v "kk")) (idx "lre" (v "best"))
              sAssign (idx "lre" (v "best")) (v "tr")
              sLet "ti" (idx "lim" (v "kk"))
              sAssign (idx "lim" (v "kk")) (idx "lim" (v "best"))
              sAssign (idx "lim" (v "best")) (v "ti") ] ]
    let body =
        blockE (copyIn @ scratch @ hess @ francis @ extract,
                   Some (tupleE [ v "lre"; v "lim" ]))
    mkFunc name [ ("a", tyFloatMat n n) ] (TyTuple [ tyFloatArr n; tyFloatArr n ]) body

// ============================================================================
// unfold / mode_product — THE rank-generic generators: one F# definition,
// instantiated per tensor rank by the elaborator (the PplElaborate
// moment-tower pattern). All strides/weights are computed here in F# and
// baked as int literals; the generated code is a straight rank-N nest.
// ============================================================================

/// Mode-n matricization for a fixed shape (Kolda–Bader, 0-based):
/// X(i_0..i_{N-1}) -> M(i_mode, j), j = Σ_{k≠mode} i_k·J_k with
/// J_k = Π_{m<k, m≠mode} I_m. Rows = I_mode, cols = Π_{k≠mode} I_k.
let unfoldDecl (name: string) (dims: int list) (mode: int) : FunctionDecl =
    let nRank = dims.Length
    let rows = dims.[mode]
    let cols = prodInts dims / rows
    let jw =
        [ for k in 0 .. nRank - 1 ->
            if k = mode then 0
            else prodInts [ for mm in 0 .. k - 1 do if mm <> mode then yield dims.[mm] ] ]
    let ivars = [ for k in 0 .. nRank - 1 -> sprintf "i%d" k ]
    let colIdx =
        [ for k in 0 .. nRank - 1 do
            if k <> mode then yield mul (v ivars.[k]) (iLit jw.[k]) ]
        |> List.reduce add
    let flatIdx = add (mul (v ivars.[mode]) (iLit cols)) colIdx
    let inner = [ sAssign (idx "o" flatIdx) (syn (ExprApp (v "x", ivars |> List.map v))) ]
    let body =
        blockE (
            sLetMut "o" (zerosLit (rows * cols)) :: loopNest ivars dims inner,
            Some (nestedFromFlat "o" rows cols))
    mkFunc name [ ("x", tyFloatTensor dims) ] (tyFloatMat rows cols) body

/// Mode-n product for a fixed shape: Y = X ×_mode U with U: jOut×I_mode —
/// Y(..., r, ...) = Σ_t U(r, t)·X(..., t, ...). Output shape = dims with
/// dims[mode] := jOut. The nest runs over the OUTPUT multi-index with an
/// inner contraction loop; `acc` is hoisted (no `let mut` in loop bodies).
let modeProductDecl (name: string) (dims: int list) (mode: int) (jOut: int) : FunctionDecl =
    let nRank = dims.Length
    let iMode = dims.[mode]
    let outDims = dims |> List.mapi (fun k d -> if k = mode then jOut else d)
    let outStrides = [ for k in 0 .. nRank - 1 -> prodInts (List.skip (k + 1) outDims) ]
    let ovars = [ for k in 0 .. nRank - 1 -> sprintf "o%d" k ]
    let flatOut =
        [ for k in 0 .. nRank - 1 -> mul (v ovars.[k]) (iLit outStrides.[k]) ]
        |> List.reduce add
    let readX =
        syn (ExprApp (v "x", [ for k in 0 .. nRank - 1 -> if k = mode then v "t" else v ovars.[k] ]))
    let inner =
        [ sAssign (v "acc") (fLit 0.0)
          sFor "t" 0 iMode [ sAccum (v "acc") (mul (idx2 "u" (v ovars.[mode]) (v "t")) readX) ]
          sAssign (idx "o" flatOut) (v "acc") ]
    let body =
        blockE (
            sLetMut "o" (zerosLit (prodInts outDims))
            :: sLetMut "acc" (fLit 0.0)
            :: loopNest ovars outDims inner,
            Some (nestedFromFlatN "o" outDims 0))
    mkFunc name [ ("x", tyFloatTensor dims); ("u", tyFloatMat jOut iMode) ]
        (tyFloatTensor outDims) body

// ============================================================================
// hosvd — orchestration over generated helpers (never inlined: the body
// only chains calls, respecting the g++ compile budget)
// ============================================================================

/// Mode-n Gram for a fixed shape: G(a,b) = Σ_{other indices} X(..a..)·X(..b..)
/// (= X_(n)·X_(n)ᵀ by the Kolda–Bader identity), formed directly by a loop
/// nest without materializing the unfolding.
let gramDecl (name: string) (dims: int list) (mode: int) : FunctionDecl =
    let nRank = dims.Length
    let g = dims.[mode]
    let otherVars = [ for k in 0 .. nRank - 1 do if k <> mode then yield sprintf "i%d" k ]
    let otherExts = [ for k in 0 .. nRank - 1 do if k <> mode then yield dims.[k] ]
    let readAt (modeVar: string) =
        syn (ExprApp (v "x", [ for k in 0 .. nRank - 1 -> if k = mode then v modeVar else v (sprintf "i%d" k) ]))
    let othersNest = loopNest otherVars otherExts [ sAccum (v "acc") (mul (readAt "a") (readAt "b")) ]
    let body =
        blockE (
            [ sLetMut "gm" (zerosLit (g * g))
              sLetMut "acc" (fLit 0.0)
              sFor "a" 0 g
                [ sFor "b" 0 g
                    ([ sAssign (v "acc") (fLit 0.0) ]
                     @ othersNest
                     @ [ sAssign (flat "gm" g (v "a") (v "b")) (v "acc") ]) ] ],
            Some (nestedFromFlat "gm" g g))
    mkFunc name [ ("x", tyFloatTensor dims) ] (tyFloatMat g g) body

/// Transposed truncated mode product: Y(..., r, ...) = Σ_t Q(t, r)·X(..., t, ...)
/// with Q the FULL I_mode×I_mode eigenvector matrix and r < rOut — contracts
/// a mode with U_nᵀ (leading rOut columns of Q) without materializing either
/// the transpose or the column slice.
let modeProdTDecl (name: string) (dims: int list) (mode: int) (rOut: int) : FunctionDecl =
    let nRank = dims.Length
    let iMode = dims.[mode]
    let outDims = dims |> List.mapi (fun k d -> if k = mode then rOut else d)
    let outStrides = [ for k in 0 .. nRank - 1 -> prodInts (List.skip (k + 1) outDims) ]
    let ovars = [ for k in 0 .. nRank - 1 -> sprintf "o%d" k ]
    let flatOut =
        [ for k in 0 .. nRank - 1 -> mul (v ovars.[k]) (iLit outStrides.[k]) ]
        |> List.reduce add
    let readX =
        syn (ExprApp (v "x", [ for k in 0 .. nRank - 1 -> if k = mode then v "t" else v ovars.[k] ]))
    let inner =
        [ sAssign (v "acc") (fLit 0.0)
          sFor "t" 0 iMode [ sAccum (v "acc") (mul (idx2 "q" (v "t") (v ovars.[mode])) readX) ]
          sAssign (idx "o" flatOut) (v "acc") ]
    let body =
        blockE (
            sLetMut "o" (zerosLit (prodInts outDims))
            :: sLetMut "acc" (fLit 0.0)
            :: loopNest ovars outDims inner,
            Some (nestedFromFlatN "o" outDims 0))
    mkFunc name [ ("x", tyFloatTensor dims); ("q", tyFloatMat iMode iMode) ]
        (tyFloatTensor outDims) body

/// (Truncated) HOSVD for a fixed shape: per mode n, U_n = leading R_n
/// eigenvectors (descending eigenvalues) of the mode-n Gram; core =
/// X ×₀ U0ᵀ ×₁ U1ᵀ ... — X ≈ core ×₀ U0 ×₁ U1 .... Returns
/// (core, U0, ..., U{N-1}) — the tuple arity is FIXED per rank at
/// elaboration time; rank polymorphism lives in this F# generator.
///
/// The body is pure orchestration: it calls the per-mode generated Gram,
/// eigh, and transposed-mode-product helpers (whose names the elaborator
/// passes in; the eigh helpers are ensure-shared with user m.eigh calls
/// of the same shape) and slices factor columns as nested literals.
let hosvdDecl (name: string) (dims: int list) (ranks: int list)
              (gramNames: string list) (eighNames: string list) (mptNames: string list)
    : FunctionDecl =
    let nRank = dims.Length
    let stmts =
        [ for mode in 0 .. nRank - 1 do
            yield sLet (sprintf "g%d" mode) (syn (ExprApp (v gramNames.[mode], [ v "x" ])))
            yield StmtLet { Pattern = synPat (PatTuple [ synPat (PatVar (sprintf "q%d" mode)); synPat (PatVar (sprintf "l%d" mode)) ])
                            Type = None
                            Value = syn (ExprApp (v eighNames.[mode], [ v (sprintf "g%d" mode) ]))
                            Mutability = BindLet } ]
        @ [ for mode in 0 .. nRank - 1 ->
              let src = if mode = 0 then "x" else sprintf "c%d" (mode - 1)
              sLet (sprintf "c%d" mode) (syn (ExprApp (v mptNames.[mode], [ v src; v (sprintf "q%d" mode) ]))) ]
        @ [ for mode in 0 .. nRank - 1 ->
              // U_mode = leading ranks[mode] columns of q_mode (literal-index
              // reads; bound to a let — tuple-of-variables boundary)
              sLet (sprintf "u%d" mode)
                   (syn (ExprArrayLit
                        [ for i in 0 .. dims.[mode] - 1 ->
                            syn (ExprArrayLit
                                [ for r in 0 .. ranks.[mode] - 1 ->
                                    idx2 (sprintf "q%d" mode) (iLit i) (iLit r) ]) ])) ]
    let retTuple =
        tupleE(v (sprintf "c%d" (nRank - 1)) :: [ for mode in 0 .. nRank - 1 -> v (sprintf "u%d" mode) ])
    let retTy =
        TyTuple (tyFloatTensor ranks :: [ for mode in 0 .. nRank - 1 -> tyFloatMat dims.[mode] ranks.[mode] ])
    mkFunc name [ ("x", tyFloatTensor dims) ] retTy (blockE (stmts, Some retTuple))
