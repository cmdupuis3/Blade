/// Math-module decl builders: dense linear-algebra and tensor-decomposition kernels (one-sided Jacobi SVD, cyclic Jacobi
/// eigh, mode-n unfolding, mode products, HOSVD) synthesized as plain Blade source.
///
/// Rank polymorphism lives HERE: each builder is ONE order-generic F# function that emits a fixed-rank Blade FunctionDecl per
/// requested tensor order (F# loops over the order, the generated code is straight per-rank nests).
///
/// House style for generated bodies (long scalar let-chains inline exponentially in codegen -- never emit them):
///   - flat rank-1 `mut` work arrays with static-literal strides,
///   - for-in loop nests (runtime-expression bounds allowed),
///   - if-EXPRESSIONS for guards (no statement-level control flow),
///   - rank-2+ inputs copied in via `w(i*n+j) = a(i, j)`,
///   - BUT where every cell is written once (and, for a sum, from +0.0 in ascending order) the body is a map / seeded
///     `(+)` fold instead (`mapRanges`, `sumRange`; P3-24, docs/features.md "Domain-elaborated routine forms"): the
///     for-in nests below are the in-place sequential algorithms and the forms a map measured slower in,
///   - work arrays zero-initialized and rank-2+ outputs reshaped by `method_for(range<..>)` maps (`zerosLit`,
///     `reshapeFlat`) -- O(1) source text per buffer, never an O(cells) literal.
///
/// Unfolding convention (documented once, used everywhere): Kolda-Bader mode-n matricization, 0-based -- X(i_0..i_{N-1}) maps
/// to M(i_mode, j) with j = Sum_{k<>mode} i_k * J_k, J_k = Prod_{m<k, m<>mode} I_m.
module Blade.Math.Decls

open Blade.Ast

// AST construction helpers (mirroring MLElaborate's style)
let v (n: string) = syn (ExprVar n)
// These generated kernels have an explicit Float64/Complex128 contract. Their
// coefficients, work buffers, and Int64 coordinates must not inherit user literal defaults.
let fLit (x: float) = syn (ExprApp (v "Float64", [syn (ExprLit (LitFloat x))]))
let iLit (n: int) = syn (ExprApp (v "Int64", [syn (ExprLit (LitInt (int64 n)))]))
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
/// for-in with expression bounds (runtime lower bounds proven: ml's tpDecl iterates table-driven ranges).
let sForE var loE hiE body = StmtForIn (var, syn (ExprDotDot (loE, hiE)), body)
/// A zero-initialized flat work array of n cells in O(1) source text: `method_for(range<Idx<n>>) <@> lambda(i) -> 0.0
/// |> compute`. It used to be an n-element `[0.0, ...]` literal -- at eigh n = 150 two of those plus the output reshape
/// emitted ~68k C++ lines and g++ timed out. (Not `replicate(n, pure(0.0))`: lowering expands a replicate into an
/// n-way sequence, the same O(n) text again.) Under reverse-mode AD the eager-map lowering turns this back into its
/// zero-literal construction buffer, exactly the shape the literal had.
let zerosLit (n: int) =
    let ps : LambdaParam list = [ { Name = "__zi"; Type = None; Default = None; NameSpan = noSpan } ]
    syn (ExprCompute (syn (ExprBinOp (Elementwise, OpApply,
                                      syn (ExprMethodFor [ syn (ExprRange [ TyIdx (iLit n) ]) ]),
                                      syn (ExprLambda (ps, None, fLit 0.0))))))
/// `zerosLit`'s Int64 twin: n cells of `Int64(0)` (the index permutation of `stableDescPerm`).
let zerosIntLit (n: int) =
    let ps : LambdaParam list = [ { Name = "__zi"; Type = None; Default = None; NameSpan = noSpan } ]
    syn (ExprCompute (syn (ExprBinOp (Elementwise, OpApply,
                                      syn (ExprMethodFor [ syn (ExprRange [ TyIdx (iLit n) ]) ]),
                                      syn (ExprLambda (ps, None, iLit 0))))))
let tyFloat = TyNamed ("Float64", [])
let tyFloatArr (n: int) = TyArray (tyFloat, [ TyIdx (iLit n) ])

let mkFunc name (ps: (string * TypeExpr) list) retTy body : FunctionDecl =
    { Name = name
      TypeParams = []
      Params = ps |> List.map (fun (n, t) -> { Name = n; Type = Some t; Mutability = Immutable; Default = None; NameSpan = noSpan })
      WhereClause = None
      ReturnType = Some retTy
      Body = body
      IsStatic = false
      NameSpan = noSpan }

let absE e = syn (ExprApp (v "abs", [e]))
let sqrtE e = syn (ExprApp (v "sqrt", [e]))
let negOne = syn (ExprUnaryOp (OpNeg, fLit 1.0))
let cmp op a b = syn (ExprBinOp (Elementwise, op, a, b))
/// a(i, j) -- multi-subscript read (runtime indices proven, func-arrays/010).
let idx2 (arr: string) (i: Expr) (j: Expr) = syn (ExprApp (v arr, [i; j]))
/// Flat row-major cell arr(i*stride + j) of a rank-1 work array.
let flat (arr: string) (stride: int) (i: Expr) (j: Expr) =
    idx arr (add (mul i (iLit stride)) j)
/// Rank-N reshape of a flat row-major work array: `method_for(range<Idx<d0>>, ..., range<Idx<dk>>) <@> lambda(i0, ..,
/// ik) -> arr(i0*s0 + ... + ik) |> compute` -- O(rank) source text. It replaces a nested array literal of one runtime
/// read per cell (O(cells) text; see `zerosLit`). The cells, and so the values, are the same.
let reshapeFlat (arr: string) (dims: int list) : Expr =
    let names = dims |> List.mapi (fun k _ -> $"__rs{k}")
    let strides = [ for k in 0 .. dims.Length - 1 -> List.fold (*) 1 (List.skip (k + 1) dims) ]
    let flatE =
        List.map2 (fun nm st -> if st = 1 then v nm else mul (v nm) (iLit st)) names strides
        |> List.reduce add
    let ops = dims |> List.map (fun d -> syn (ExprRange [ TyIdx (iLit d) ]))
    let ps : LambdaParam list = names |> List.map (fun nm -> { Name = nm; Type = None; Default = None; NameSpan = noSpan })
    syn (ExprCompute (syn (ExprBinOp (Elementwise, OpApply, syn (ExprMethodFor ops), syn (ExprLambda (ps, None, idx arr flatE))))))

/// Rank-2 reshape of a flat work array (corpus math/005).
let nestedFromFlat (arr: string) (rows: int) (cols: int) : Expr = reshapeFlat arr [ rows; cols ]
let tyFloatMat (rows: int) (cols: int) =
    TyArray (tyFloat, [ TyIdx (iLit rows); TyIdx (iLit cols) ])

/// Default Jacobi sweep budget: converges quadratically once sweeps begin; 6-8 reach machine epsilon at n <= ~100, 10 gives margin.
let defaultSweeps = 10

/// The convergence verdict a fixed-budget iteration checks once its budget is
/// spent: `__math_converged(cond, msg)` types as a BL8008 runtime check
/// (TypeCheckInfer), so a NaN/Inf operand or a matrix the budget cannot
/// finish PANICS in every lane -- the code the LAPACK route raises on a
/// non-zero info -- instead of returning a silently unconverged result.
let sConverged (cond: Expr) (msg: string) =
    StmtExpr (syn (ExprApp (v "__math_converged", [ cond; syn (ExprLit (LitString msg)) ])))

/// `sConverged`'s budget form: a false `cond` panics BL8010, the language's "iteration budget ran out" code (the one a
/// `while`-guarded recursion raises). For an iteration whose operand was already checked finite, so running out of
/// iterations is the only way to fail it (eig's Francis QR).
let sBudgetSpent (cond: Expr) (msg: string) =
    StmtExpr (syn (ExprApp (v "__math_converged", [ cond; syn (ExprLit (LitString msg)); syn (ExprLit (LitString "BL8010")) ])))

/// Relative tolerance of those verdicts: far above the 1e-15 the rotations
/// aim for (a converged factorization is many orders inside it), far below
/// any residual that would make the result wrong in print.
let convergedTol = 1.0e-8

/// `cond`, but false when `x` is not finite: `x - x` is 0.0 exactly for a
/// finite x and NaN for an infinite or NaN one, so an Inf operand -- whose
/// norms are Inf, against which every relative test passes (inf <= tol * inf)
/// -- is refused too.
let finiteAnd (x: Expr) (cond: Expr) =
    syn (ExprIf (cmp OpEq (sub x x) (fLit 0.0), cond, syn (ExprLit (LitBool false))))

/// Statements setting the mut `amaxName` to max |cell| over the flat work
/// array cells `cellAt i j` (i < rows, j < cols), and `invName` (a fresh let)
/// to 1/amax (0 for an all-zero array). The convergence verdicts measure the
/// array SCALED by inv: squaring raw entries overflows at scales the solvers
/// themselves handle (math/091's 1e160 eigh), which would read as "not
/// converged". A NaN cell is skipped by the max but poisons every scaled sum.
let maxAbsScale (amaxName: string) (invName: string) (rows: int) (cols: int) (cellAt: Expr -> Expr -> Expr) : Stmt list =
    [ sAssign (v amaxName) (fLit 0.0)
      sFor "i" 0 rows
        [ sFor "j" 0 cols
            [ sLet "mag" (absE (cellAt (v "i") (v "j")))
              sAssign (v amaxName) (syn (ExprIf (cmp OpGt (v "mag") (v amaxName), v "mag", v amaxName))) ] ]
      sLet invName (syn (ExprIf (cmp OpGt (v amaxName) (fLit 0.0), divE (fLit 1.0) (v amaxName), fLit 0.0))) ]

let prodInts (xs: int list) = List.fold (*) 1 xs
/// Rank-N dense tensor type: Array<Float like Idx<d0>, Idx<d1>, ...>.
let tyFloatTensor (dims: int list) =
    TyArray (tyFloat, dims |> List.map (fun d -> TyIdx (iLit d)))
/// Rank-N reshape of a flat row-major work array (the rank-N generalization of nestedFromFlat); `offset` must be 0.
let nestedFromFlatN (arr: string) (dims: int list) (offset: int) : Expr =
    if dims.IsEmpty then failwith "nestedFromFlatN: empty dims"
    if offset <> 0 then failwith "nestedFromFlatN: only a whole-buffer reshape (offset 0) is supported"
    reshapeFlat arr dims
/// N-deep loop nest: vars/extents outermost first, around the given body.
let loopNest (vars: string list) (extents: int list) (body: Stmt list) : Stmt list =
    List.foldBack2 (fun name extent acc -> [ sFor name 0 extent acc ]) vars extents body

// Span-stamping wrappers for the raw combinators used in decl bodies (full-span AST): each attributes to the ambient synthSpan.
let ifE (c, t, f) = syn (ExprIf (c, t, f))
let blockE (stmts, fin) = syn (ExprBlock (stmts, fin))
let tupleE (xs: Expr list) = syn (ExprTuple xs)

/// The descending order svd / eigh / eig return their values (and value-carrying columns) in: STABLE -- descending by
/// the key, equal keys in EXTRACTION order (the order the algorithm produced them before the sort; R26). Fills the mut
/// Int64 work array `perm` (n cells, `zerosIntLit`, declared by the caller) so that output position kk takes extracted
/// position perm(kk); the caller then applies it with ONE gather per output (values, and each column-carrying matrix).
///
/// An insertion sort of the INDEX array: index kk moves down past every strictly smaller key by adjacent compare-swaps
/// of perm entries (an equal key never swaps, so ties keep their order) -- O(n^2) scalar steps, the selection sort's
/// count, and no column moves. Each pass runs to position 0 (no statement-level control flow): once the new index stops,
/// the prefix is already sorted and the rest of the pass swaps nothing. Always a permutation, whatever the keys hold.
let stableDescPerm (perm: string) (n: int) (keyAt: Expr -> Expr) : Stmt list =
    [ sFor "j" 0 n [ sAssign (idx perm (v "j")) (v "j") ]
      sFor "kk" 1 n
        [ sForE "jj" (iLit 0) (v "kk")
            [ sLet "pj" (sub (sub (v "kk") (iLit 1)) (v "jj"))
              sLet "pa" (idx perm (v "pj"))
              sLet "pb" (idx perm (add (v "pj") (iLit 1)))
              // swap iff the later key is strictly larger: d = (pb - pa) * Int64(key(pb) > key(pa)), exact integer
              // arithmetic (no if-values: the write rules' cost is per `if`)
              sLet "pd" (mul (sub (v "pb") (v "pa"))
                             (syn (ExprApp (v "Int64", [ cmp OpGt (keyAt (v "pb")) (keyAt (v "pa")) ]))))
              sAssign (idx perm (v "pj")) (add (v "pa") (v "pd"))
              sAssign (idx perm (add (v "pj") (iLit 1))) (sub (v "pb") (v "pd")) ] ] ]

// matmul is NOT synthesized here: `math.matmul` is a first-class codegen intrinsic (MathElaborate rewrites the call to the
// `__math_matmul` marker, TypeCheck.inferMatmul types it, codegen emits one `blade_linalg::blade_matmul` call). The native
// fallback reproduces the naive i, j, t-ascending triple loop with one accumulator seeded at +0.0, so values match a
// BLAS-linked build's blocked/microkernel dgemm to print precision. Every OTHER op in this file stays synthesized.

// svd (one-sided / Hestenes cyclic Jacobi)

/// Thin SVD for fixed (m, n, sweeps), m >= n: A ~= U*diag(S)*V^T with S descending (stable: a repeated singular value
/// keeps its columns' sweep order, `stableDescPerm`), U m x n (columns orthonormal; zero sigma -> zero column), V n x n
/// (COLUMNS are the right singular vectors: A(i,j) = Sum_k U(i,k)*S(k)*V(j,k)).
///
/// One-sided Jacobi on a working copy W: for each column pair (p, q) the rotation angle comes from the 2x2 Gram entries
/// a=|wp|^2, b=|wq|^2, c=wp.wq via the stable smaller-root formula (t^2 + 2*zeta*t - 1 = 0, zeta=(b-a)/2c); a pair with
/// |c| <= 1e-15*sqrt(a)*sqrt(b) guards (split roots: sqrt(a*b) overflows once the entries pass ~1e77, which froze every
/// pair as converged and returned non-orthogonal factors) to the identity through if-EXPRESSIONS. V accumulates the same column rotations from an
/// identity. Post: S = column norms, ordered by `stableDescPerm` and gathered into S / U / V, U = normalized columns, sign fix (first
/// row attaining max |entry| per U column made positive; U and V columns flip together).
///
/// Scratch muts are hoisted to the top and re-zeroed per use (no `let mut` inside loop bodies).
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
                                                (mul (fLit 1.0e-15) (mul (sqrtE (v "aa")) (sqrtE (v "bb")))))
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
              // converged: the columns of w are mutually orthogonal, sqrt(sum c_pq^2) <= tol * ||w||_F^2
              // (a global scale, so noise-level columns of a rank-deficient operand cannot trip it), measured on
              // w / max|w| so no square overflows; NaN fails it, an Inf max fails finiteAnd
              sLetMut "wmax" (fLit 0.0) ]
            @ maxAbsScale "wmax" "winv" m n wAt
            @ [
              sLetMut "offc" (fLit 0.0)
              sLetMut "nrm2" (fLit 0.0)
              sFor "j" 0 n
                [ sFor "i" 0 m
                    [ sLet "wj" (mul (wAt (v "i") (v "j")) (v "winv"))
                      sAccum (v "nrm2") (mul (v "wj") (v "wj")) ] ]
              sFor "p" 0 (n - 1)
                [ sForE "q" (add (v "p") (iLit 1)) (iLit n)
                    [ sAssign (v "cc") (fLit 0.0)
                      sFor "i" 0 m [ sAccum (v "cc") (mul (mul (wAt (v "i") (v "p")) (v "winv"))
                                                          (mul (wAt (v "i") (v "q")) (v "winv"))) ]
                      sAccum (v "offc") (mul (v "cc") (v "cc")) ] ]
              sConverged (finiteAnd (v "wmax") (cmp OpLe (sqrtE (v "offc")) (mul (fLit convergedTol) (v "nrm2"))))
                  "svd: the one-sided Jacobi iteration did not converge within its sweep budget -- the operand holds NaN/Inf or the budget is too small for it"
              // singular values, in extraction order = column norms of w
              sLetMut "sraw" (zerosLit n)
              sFor "j" 0 n
                [ sAssign (v "aa") (fLit 0.0)
                  sFor "i" 0 m
                    [ sLet "wj" (wAt (v "i") (v "j"))
                      sAccum (v "aa") (mul (v "wj") (v "wj")) ]
                  sAssign (idx "sraw" (v "j")) (sqrtE (v "aa")) ]
              // the stable descending order (ties keep column order), applied by one gather per output below
              sLetMut "perm" (zerosIntLit n) ]
            @ stableDescPerm "perm" n (idx "sraw")
            @ [
              sLetMut "s" (zerosLit n)
              sFor "j" 0 n [ sAssign (idx "s" (v "j")) (idx "sraw" (idx "perm" (v "j"))) ]
              // U = normalized columns, gathered: U's column j is w's column perm(j) (zero sigma -> zero column, documented)
              sLetMut "u" (zerosLit (m * n))
              sFor "j" 0 n
                [ sLet "pc" (idx "perm" (v "j"))
                  sLet "inv" (ifE (cmp OpGt (idx "s" (v "j")) (fLit 1.0e-300),
                                      divE (fLit 1.0) (idx "s" (v "j")), fLit 0.0))
                  sFor "i" 0 m [ sAssign (uAt (v "i") (v "j")) (mul (wAt (v "i") (v "pc")) (v "inv")) ] ]
              // sign fix: first row attaining max |entry| per U column made positive; U and V columns flip together
              // (first-max); V's column j is gathered from vv's column perm(j) as it is flipped
              sLetMut "vs" (zerosLit (n * n))
              sFor "j" 0 n
                [ sAssign (v "bigv") (fLit 0.0)
                  sAssign (v "best") (iLit 0)
                  sFor "i" 0 m
                    [ sLet "mag" (absE (uAt (v "i") (v "j")))
                      sAssign (v "best") (ifE (cmp OpGt (v "mag") (v "bigv"), v "i", v "best"))
                      sAssign (v "bigv") (ifE (cmp OpGt (v "mag") (v "bigv"), v "mag", v "bigv")) ]
                  sLet "flip" (ifE (cmp OpLt (uAt (v "best") (v "j")) (fLit 0.0), negOne, fLit 1.0))
                  sLet "pc" (idx "perm" (v "j"))
                  sFor "i" 0 m [ sAssign (uAt (v "i") (v "j")) (mul (uAt (v "i") (v "j")) (v "flip")) ]
                  sFor "i" 0 n [ sAssign (flat "vs" n (v "i") (v "j")) (mul (vAt (v "i") (v "pc")) (v "flip")) ] ]
              // assemble rank-2 outputs as named lets: array literals are NOT supported inside a tuple (codegen IRArrayLit gap)
              sLet "uo" (nestedFromFlat "u" m n)
              sLet "vo" (nestedFromFlat "vs" n n) ],
            Some (tupleE [ v "uo"; v "s"; v "vo" ]))
    mkFunc name [ ("a", tyFloatMat m n) ]
        (TyTuple [ tyFloatMat m n; tyFloatArr n; tyFloatMat n n ]) body

// eigh (cyclic two-sided Jacobi, symmetric input assumed)

/// Symmetric eigendecomposition for fixed (n, sweeps): S ~= Q*diag(LAM)*Q^T with LAM descending (stable: a repeated
/// eigenvalue's eigenvector columns keep their diagonal order, `stableDescPerm`) and Q's COLUMNS the eigenvectors.
/// Symmetry of the input is ASSUMED, not checked.
///
/// Cyclic two-sided Jacobi on a working copy AW: for pivot (p, q) the angle comes from theta = (a_qq - a_pp)/(2*a_pq) via
/// the same stable smaller-root formula as svd (t^2 + 2*theta*t - 1 = 0); |a_pq| <= 1e-15*(sqrt|a_pp|*sqrt|a_qq| + 1e-150)
/// guards to the identity (the tiny absolute floor keeps the guard alive on indefinite/zero diagonals; the roots are split
/// so the product cannot overflow at large scale). Each rotation updates AW's
/// columns AND rows (AW <- R^T AW R) and accumulates the column rotation into QM. Post: eigenvalues = diagonal,
/// ordered by `stableDescPerm` and gathered into LAM / Q, sign fix on Q's columns (first row attaining max |entry| made positive).
/// Mirrors math/Jacobi.fs `eigh` operation-for-operation, so values agree to the ulp.
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
                                                     (add (mul (sqrtE (absE (v "app"))) (sqrtE (absE (v "aqq")))) (fLit 1.0e-150))))
                          sLet "theta" (divE (sub (v "aqq") (v "app"))
                                             (ifE (v "conv", fLit 1.0, mul (fLit 2.0) (v "apq"))))
                          sLet "tt" (divE (ifE (cmp OpGe (v "theta") (fLit 0.0), fLit 1.0, negOne))
                                          (add (absE (v "theta"))
                                               (sqrtE (add (fLit 1.0) (mul (v "theta") (v "theta"))))))
                          sLet "cs" (ifE (v "conv", fLit 1.0,
                                             divE (fLit 1.0) (sqrtE (add (fLit 1.0) (mul (v "tt") (v "tt"))))))
                          sLet "sn" (ifE (v "conv", fLit 0.0, mul (v "cs") (v "tt")))
                          // AW <- AW*R (columns p, qq), scalar-buffered
                          sFor "i" 0 n
                            [ sLet "tp" (aAt (v "i") (v "p"))
                              sLet "tq" (aAt (v "i") (v "qq"))
                              sAssign (aAt (v "i") (v "p")) (sub (mul (v "cs") (v "tp")) (mul (v "sn") (v "tq")))
                              sAssign (aAt (v "i") (v "qq")) (add (mul (v "sn") (v "tp")) (mul (v "cs") (v "tq"))) ]
                          // AW <- R^T*AW (rows p, qq)
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
              // converged: the off-diagonal is negligible, sqrt(sum_{p<q} a_pq^2) <= tol * ||aw||_F, measured on
              // aw / max|aw| so no square overflows; NaN fails it, an Inf max fails finiteAnd
              sLetMut "amx" (fLit 0.0) ]
            @ maxAbsScale "amx" "ainv" n n aAt
            @ [
              sLetMut "offa" (fLit 0.0)
              sLetMut "nrma" (fLit 0.0)
              sFor "i" 0 n
                [ sFor "j" 0 n
                    [ sLet "aij" (mul (aAt (v "i") (v "j")) (v "ainv"))
                      sAccum (v "nrma") (mul (v "aij") (v "aij")) ] ]
              sFor "p" 0 (n - 1)
                [ sForE "qq" (add (v "p") (iLit 1)) (iLit n)
                    [ sLet "apq" (mul (aAt (v "p") (v "qq")) (v "ainv"))
                      sAccum (v "offa") (mul (v "apq") (v "apq")) ] ]
              sConverged (finiteAnd (v "amx") (cmp OpLe (sqrtE (v "offa")) (mul (fLit convergedTol) (sqrtE (v "nrma")))))
                  "eigh: the Jacobi iteration did not converge within its sweep budget -- the operand holds NaN/Inf or the budget is too small for it"
              // eigenvalues, in extraction order = diagonal
              sLetMut "ld" (zerosLit n)
              sFor "j" 0 n [ sAssign (idx "ld" (v "j")) (aAt (v "j") (v "j")) ]
              // the stable descending order (ties keep diagonal order), applied by one gather per output below
              sLetMut "perm" (zerosIntLit n) ]
            @ stableDescPerm "perm" n (idx "ld")
            @ [
              sLetMut "lam" (zerosLit n)
              sFor "j" 0 n [ sAssign (idx "lam" (v "j")) (idx "ld" (idx "perm" (v "j"))) ]
              // sign fix on the eigenvector columns: Q's column j is QM's column perm(j), gathered as it is flipped
              sLetMut "qs" (zerosLit (n * n))
              sFor "j" 0 n
                [ sLet "pc" (idx "perm" (v "j"))
                  sAssign (v "bigv") (fLit 0.0)
                  sAssign (v "best") (iLit 0)
                  sFor "i" 0 n
                    [ sLet "mag" (absE (qAt (v "i") (v "pc")))
                      sAssign (v "best") (ifE (cmp OpGt (v "mag") (v "bigv"), v "i", v "best"))
                      sAssign (v "bigv") (ifE (cmp OpGt (v "mag") (v "bigv"), v "mag", v "bigv")) ]
                  sLet "flip" (ifE (cmp OpLt (qAt (v "best") (v "pc")) (fLit 0.0), negOne, fLit 1.0))
                  sFor "i" 0 n [ sAssign (flat "qs" n (v "i") (v "j")) (mul (qAt (v "i") (v "pc")) (v "flip")) ] ]
              // tuple-of-variables boundary (IRArrayLit-in-tuple gap)
              sLet "qo" (nestedFromFlat "qs" n n) ],
            Some (tupleE [ v "qo"; v "lam" ]))
    mkFunc name [ ("a", tyFloatMat n n) ]
        (TyTuple [ tyFloatMat n n; tyFloatArr n ]) body

// expm (scaling and squaring over the degree-18 Taylor polynomial)

/// Matrix exponential e^A for a fixed n, solve-free: scale X = 2^-s A so that ||X||_1 <= theta_18 = 1.0909, evaluate
/// the degree-18 Taylor polynomial by Horner (Y <- I + X Y / k, k = 18 .. 1), square s times. In exact arithmetic
/// T_18(2^-s A)^(2^s) = e^(A + dA) with ||dA||_1 <= u ||A||_1 (Higham 2005's backward-error argument transplanted to
/// Taylor; theta_18 computed with exact rational coefficients -- docs/research/latent-symmetry/RIGOR.md B6.2). s is the
/// smallest s >= 0 with ||A||_1 / 2^s <= theta, found by a 64-step bounded halving loop with if-EXPRESSION updates
/// (no statement-level control flow in generated bodies; 64 halvings cover ||A||_1 up to theta * 2^64) and drives a
/// runtime-bounded squaring loop. The squaring phase can lose accuracy for highly non-normal A (Al-Mohy & Higham
/// 2010); it is benign for the near-antisymmetric generators the symmetry-discovery lane exponentiates. Plain products
/// throughout, so forward mode / a later reverse rule differentiate the algorithm itself -- the Fréchet derivative of
/// the computed approximant (B6.3).
let expmDecl (name: string) (n: int) : FunctionDecl =
    let xAt = flat "x" n
    let yAt = flat "y" n
    let tAt = flat "t" n
    let theta = 1.0909
    // t <- p * q (flat n x n operands; one accumulator seeded at +0.0, ascending kk -- the matmul convention)
    let product (p: Expr -> Expr -> Expr) (q: Expr -> Expr -> Expr) =
        sFor "i" 0 n
            [ sFor "j" 0 n
                [ sAssign (v "acc") (fLit 0.0)
                  sFor "kk" 0 n [ sAccum (v "acc") (mul (p (v "i") (v "kk")) (q (v "kk") (v "j"))) ]
                  sAssign (tAt (v "i") (v "j")) (v "acc") ] ]
    let copyTToY = sFor "i" 0 n [ sFor "j" 0 n [ sAssign (yAt (v "i") (v "j")) (tAt (v "i") (v "j")) ] ]
    // one Horner step: y <- I + (x y) / k
    let hornerStep (k: int) =
        [ product xAt yAt
          sFor "i" 0 n
            [ sFor "j" 0 n
                [ sAssign (yAt (v "i") (v "j"))
                          (add (ifE (cmp OpEq (v "i") (v "j"), fLit 1.0, fLit 0.0))
                               (divE (tAt (v "i") (v "j")) (fLit (float k)))) ] ] ]
    let body =
        blockE (
            [ sLetMut "x" (zerosLit (n * n))
              sFor "i" 0 n [ sFor "j" 0 n [ sAssign (xAt (v "i") (v "j")) (idx2 "a" (v "i") (v "j")) ] ]
              // ||A||_1 = the largest column sum of |entries|
              sLetMut "nrm" (fLit 0.0)
              sLetMut "cs" (fLit 0.0)
              sFor "j" 0 n
                [ sAssign (v "cs") (fLit 0.0)
                  sFor "i" 0 n [ sAccum (v "cs") (absE (xAt (v "i") (v "j"))) ]
                  sAssign (v "nrm") (ifE (cmp OpGt (v "cs") (v "nrm"), v "cs", v "nrm")) ]
              // s squarings and the scale 2^-s, by bounded halving
              sLetMut "s" (iLit 0)
              sLetMut "scale" (fLit 1.0)
              sFor "h" 0 64
                [ sLet "need" (cmp OpGt (mul (v "nrm") (v "scale")) (fLit theta))
                  sAssign (v "scale") (ifE (v "need", mul (v "scale") (fLit 0.5), v "scale"))
                  sAssign (v "s") (ifE (v "need", add (v "s") (iLit 1), v "s")) ]
              sFor "i" 0 n [ sFor "j" 0 n [ sAssign (xAt (v "i") (v "j")) (mul (xAt (v "i") (v "j")) (v "scale")) ] ]
              // Horner from y = I
              sLetMut "y" (zerosLit (n * n))
              sFor "i" 0 n [ sAssign (yAt (v "i") (v "i")) (fLit 1.0) ]
              sLetMut "t" (zerosLit (n * n))
              sLetMut "acc" (fLit 0.0) ]
            @ (List.concat [ for k in 18 .. -1 .. 1 -> hornerStep k ])
            @ [ // s squarings: y <- y y
                sForE "q" (iLit 0) (v "s") [ product yAt yAt; copyTToY ]
                sLet "r" (nestedFromFlat "y" n n) ],
            Some (v "r"))
    mkFunc name [ ("a", tyFloatMat n n) ] (tyFloatMat n n) body

// eig (general non-symmetric: Hessenberg + Francis double-shift QR)

/// General real eigenvalues for a fixed (n, maxIter): (LRE, LIM) sorted by DESCENDING modulus, STABLY (equal moduli
/// keep their extraction order, `stableDescPerm`), so a conjugate pair comes out adjacent with +im first, as extracted.
/// A non-finite operand panics BL8008; a budget the iteration does not converge within panics BL8010.
/// Mirrors math/Eig.fs operation-for-operation (Householder Hessenberg, windowed Francis double-shift QR with a
/// fixed budget, quasi-triangular extraction), so values agree with the oracle to the ulp.
///
/// Control flow discipline (no statement-level branching in Blade): conditional work runs as EMPTY loop ranges (runtime
/// bounds), IDENTITY rotations (cs=1, sn=0), zero Householder betas, and self-assign guards; composite conditions are Int
/// 0/1 flags composed by multiplication (OpAnd/OpOr never reach codegen). Indices that would go out of range in the
/// guarded-off case are CLAMPED to safe in-bounds dummies (lc/ec) whose writes are identity ops; n <= 2 emits no
/// Hessenberg/QR code at all (the extraction's exact 1x1/2x2 solve covers it).
///
/// No exceptional shifts (documented): adversarial cyclic-permutation spectra (exact equal-modulus roots of unity) can
/// stall within the budget; generic data-derived matrices (Koopman/EDMD) converge normally.
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
          sLetMut "skip" (iLit 0) ]
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
                  // restore exact Hessenberg zeros behind the bulge (k > lc; the k=0 flat index (k+1)*n+(k-1)=n-1 is
                  // in-bounds, and the guard self-assigns there)
                  sLet "fclean" (mul (v "okc") (bint (cmp OpGt (v "k") (v "lc"))))
                  sAssign (hAt kp1 km1) (ifE (isOne (v "fclean"), fLit 0.0, hAt kp1 km1))
                  sAssign (hAt kp2 km1) (ifE (isOne (v "fclean"), fLit 0.0, hAt kp2 km1))
                  sAssign (v "x") (hAt kp1 (v "k"))
                  sAssign (v "y") (hAt kp2 (v "k"))
                  sAssign (v "z") (ifE (cmp OpLt (v "k") (sub (v "ec") (iLit 2)), hAt kp3 (v "k"), fLit 0.0)) ]
              // final Givens on (x, y) over rows/cols ec-1, ec (identity rotation at clamped dummy indices when not chasing)
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
    // ---- converged: the operand is finite (BL8008 otherwise, as svd / eigh), then every window closed (hi <= 1)
    //      within the budget -- a stall (no exceptional shifts, so an adversarial equal-modulus spectrum can stall) or
    //      a budget too small for the operand leaves one open: BL8010, the language's "budget ran out" code ----
    let converged =
        if n < 3 then [] else
        // one scan of the operand (as the max scan it replaces): |x| - |x| is +0.0 for a finite cell and NaN for a NaN
        // or infinite one, so the sum is exactly 0.0 iff every cell is finite
        [ sLetMut "fin" (fLit 0.0)
          sFor "i" 0 n
            [ sFor "j" 0 n
                [ sLet "mag" (absE (idx2 "a" (v "i") (v "j")))
                  sAccum (v "fin") (sub (v "mag") (v "mag")) ] ]
          sConverged (cmp OpEq (v "fin") (fLit 0.0))
              "eig: the operand holds NaN/Inf -- the Francis QR iteration did not converge"
          sBudgetSpent (cmp OpLe (v "hi") (iLit 1))
              $"eig: the Francis QR iteration did not converge within its budget (MAXITER = {maxIter}) -- its spectrum stalls the shift strategy (e.g. equal-modulus roots of unity) or the budget is too small for it: raise it with m.eig(A, MAXITER)" ]
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
          // sort keys: modulus^2, computed once per eigenvalue (a conjugate pair's two keys are the same bits)
          sLetMut "mk" (zerosLit n)
          sFor "j" 0 n
            [ sAssign (idx "mk" (v "j")) (add (mul (idx "lre" (v "j")) (idx "lre" (v "j")))
                                             (mul (idx "lim" (v "j")) (idx "lim" (v "j")))) ]
          // the stable descending order by modulus (ties keep extraction order, so a conjugate pair stays adjacent and
          // +im first), applied by one gather per output
          sLetMut "perm" (zerosIntLit n) ]
        @ stableDescPerm "perm" n (idx "mk")
        @ [ sLetMut "sre" (zerosLit n)
            sLetMut "sim" (zerosLit n)
            sFor "j" 0 n
              [ sLet "pc" (idx "perm" (v "j"))
                sAssign (idx "sre" (v "j")) (idx "lre" (v "pc"))
                sAssign (idx "sim" (v "j")) (idx "lim" (v "pc")) ] ]
    let body =
        blockE (copyIn @ scratch @ hess @ francis @ converged @ extract,
                   Some (tupleE [ v "sre"; v "sim" ]))
    mkFunc name [ ("a", tyFloatMat n n) ] (TyTuple [ tyFloatArr n; tyFloatArr n ]) body

// unfold / mode_product -- the rank-generic generators: one F# definition, instantiated per tensor rank by the elaborator.
// All strides/weights are computed here in F# and baked as int literals; the generated code is a straight rank-N nest.

/// Mode-n matricization for a fixed shape (Kolda-Bader, 0-based): X(i_0..i_{N-1}) -> M(i_mode, j), j = Sum_{k<>mode} i_k*J_k
/// with J_k = Prod_{m<k, m<>mode} I_m. Rows = I_mode, cols = Prod_{k<>mode} I_k.
let unfoldDecl (name: string) (dims: int list) (mode: int) : FunctionDecl =
    let nRank = dims.Length
    let rows = dims.[mode]
    let cols = prodInts dims / rows
    let jw =
        [ for k in 0 .. nRank - 1 ->
            if k = mode then 0
            else prodInts [ for mm in 0 .. k - 1 do if mm <> mode then yield dims.[mm] ] ]
    let ivars = [ for k in 0 .. nRank - 1 -> $"i{k}" ]
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

/// BLADE_MODE_PRODUCT_DEST (rule `mode-product-dest v1`, PM31), an OPT-IN arm:
/// `1`/`on` = the mode product fills its rank-N result in place; unset/`0`/`off` = the
/// flat work array plus its rank-N reshape copy (the default). Measured on bench
/// tensorop (full, 89 x 97 x 101; 11 interleaved runs): peak 39.5 -> 32.9 MB but wall
/// 172 -> 189 ms (the nested-pointer cell store is slower than the flat one), so the
/// flat form stays the default. Read per call.
let modeProductDestEnabled () = Blade.Toolchain.envFlag "BLADE_MODE_PRODUCT_DEST" false

/// The decision record (one per generated mode-product instance).
let recordModeProductDest (name: string) (applied: bool) (dims: int list) (mode: int) (jOut: int) =
    let shape = dims |> List.map string |> String.concat " x "
    let cells = dims |> List.mapi (fun k d -> if k = mode then jOut else d) |> List.fold (*) 1
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "mode-product-dest"; Version = 1
          Span = noSpan; Subject = name
          Outcome = (if applied then Blade.Effects.Applied else Blade.Effects.Declined "opt-in (BLADE_MODE_PRODUCT_DEST=1): the in-place fill measured slower than the flat work array on bench tensorop (wall 172 -> 189 ms) while saving one tensor of peak (39.5 -> 32.9 MB)")
          Evidence =
            [ sprintf "mode-%d product of a %s tensor: the rank-%d result (%d cells) is filled in place, each cell one ascending chain from +0.0 over the mode index" mode shape dims.Length cells
              sprintf "cost: peak -%d cells (no flat work array beside the result) and one copy pass fewer; the contraction order is unchanged" cells ] }

/// The forced rank-N map over static ranges: `method_for(range<Idx<d0>>, .., range<Idx<dk>>) <@> lambda(n0, .., nk)
/// -> cell |> compute`. The declarative form of a loop nest whose every iteration writes its own output cell once
/// (class P of the P3-24 census): the cell is computed straight into the result's pool, no flat work array, no reshape.
let mapRanges (dims: int list) (names: string list) (cell: Expr) : Expr =
    let ops = dims |> List.map (fun d -> syn (ExprRange [ TyIdx (iLit d) ]))
    let ps : LambdaParam list = names |> List.map (fun nm -> { Name = nm; Type = None; Default = None; NameSpan = noSpan })
    syn (ExprCompute (syn (ExprBinOp (Elementwise, OpApply, syn (ExprMethodFor ops), syn (ExprLambda (ps, None, cell))))))

/// The seeded sum of `term` over `var` in 0 .. n-1: `reduce(method_for(range<Idx<n>>) <@> lambda(var) -> term, (+),
/// seed)` -- the fused fold (no temporary). Its kernel is the builtin `(+)`, so it is LICENSED: under
/// `BLADE_FP_REPRO=1` it is the ascending chain from `seed`, bitwise the `acc = seed; acc = acc + term` loop it
/// replaces; the default build may take its K-lane / simd form (a licensed association change).
let sumRange (n: int) (var: string) (term: Expr) (seed: Expr) : Expr =
    let ps : LambdaParam list = [ { Name = var; Type = None; Default = None; NameSpan = noSpan } ]
    let m = syn (ExprBinOp (Elementwise, OpApply, syn (ExprMethodFor [ syn (ExprRange [ TyIdx (iLit n) ]) ]),
                            syn (ExprLambda (ps, None, term))))
    syn (ExprReduce (m, syn (ExprSection OpAdd), Some seed, None))

/// A rank-N zero tensor: `method_for(range<Idx<d0>>, .., range<Idx<dk>>) <@> lambda(..) -> 0.0 |> compute`.
let zerosN (dims: int list) : Expr =
    let names = dims |> List.mapi (fun k _ -> sprintf "__zn%d" k)
    let ops = dims |> List.map (fun d -> syn (ExprRange [ TyIdx (iLit d) ]))
    let ps : LambdaParam list = names |> List.map (fun nm -> { Name = nm; Type = None; Default = None; NameSpan = noSpan })
    syn (ExprCompute (syn (ExprBinOp (Elementwise, OpApply, syn (ExprMethodFor ops), syn (ExprLambda (ps, None, fLit 0.0))))))

/// Mode-n product for a fixed shape: Y = X x_mode U with U: jOut x I_mode -- Y(..., r, ...) = Sum_t U(r,t)*X(..., t, ...).
/// Output shape = dims with dims[mode] := jOut. The nest runs over the OUTPUT multi-index; `acc` is hoisted.
let modeProductDecl (name: string) (dims: int list) (mode: int) (jOut: int) : FunctionDecl =
    let nRank = dims.Length
    let iMode = dims.[mode]
    let outDims = dims |> List.mapi (fun k d -> if k = mode then jOut else d)
    let outStrides = [ for k in 0 .. nRank - 1 -> prodInts (List.skip (k + 1) outDims) ]
    let ovars = [ for k in 0 .. nRank - 1 -> $"o{k}" ]
    let flatOut =
        [ for k in 0 .. nRank - 1 -> mul (v ovars.[k]) (iLit outStrides.[k]) ]
        |> List.reduce add
    let readX =
        syn (ExprApp (v "x", [ for k in 0 .. nRank - 1 -> if k = mode then v "t" else v ovars.[k] ]))
    // DESTINATION FORM (PM31, round 4 wave 4 lane F item F3; BLADE_MODE_PRODUCT_DEST,
    // rule `mode-product-dest v1`): the product used to fill a FLAT work array and
    // return its rank-N reshape -- a second tensor-sized pool and a copy pass. It now
    // fills the rank-N result in place (`o(o0, .., oN) = acc`) and returns it: the
    // same cells, each the same ascending chain from the same +0.0 seed, one pool.
    // OPT-IN (`1`): measured slower, see modeProductDestEnabled.
    let dest = modeProductDestEnabled ()
    recordModeProductDest name dest dims mode jOut
    let inner =
        [ sAssign (v "acc") (fLit 0.0)
          sFor "t" 0 iMode [ sAccum (v "acc") (mul (idx2 "u" (v ovars.[mode]) (v "t")) readX) ]
          (if dest then sAssign (syn (ExprApp (v "o", ovars |> List.map v))) (v "acc")
           else sAssign (idx "o" flatOut) (v "acc")) ]
    let body =
        blockE (
            sLetMut "o" (if dest then zerosN outDims else zerosLit (prodInts outDims))
            :: sLetMut "acc" (fLit 0.0)
            :: loopNest ovars outDims inner,
            Some (if dest then v "o" else nestedFromFlatN "o" outDims 0))
    mkFunc name [ ("x", tyFloatTensor dims); ("u", tyFloatMat jOut iMode) ]
        (tyFloatTensor outDims) body

// hosvd -- orchestration over generated helpers (never inlined: the body only chains calls, respecting the g++ compile budget)

/// Mode-n Gram for a fixed shape: G(a,b) = Sum_{other indices} X(..a..)*X(..b..) (= X_(n)*X_(n)^T by the Kolda-Bader
/// identity), formed directly by a loop nest without materializing the unfolding.
let gramDecl (name: string) (dims: int list) (mode: int) : FunctionDecl =
    let nRank = dims.Length
    let g = dims.[mode]
    let otherVars = [ for k in 0 .. nRank - 1 do if k <> mode then yield $"i{k}" ]
    let otherExts = [ for k in 0 .. nRank - 1 do if k <> mode then yield dims.[k] ]
    let readAt (modeVar: string) =
        syn (ExprApp (v "x", [ for k in 0 .. nRank - 1 -> if k = mode then v modeVar else v $"i{k}" ]))
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

/// Transposed truncated mode product: Y(..., r, ...) = Sum_t Q(t, r)*X(..., t, ...) with Q the FULL I_mode x I_mode
/// eigenvector matrix and r < rOut -- contracts a mode with U_n^T (leading rOut columns of Q) without materializing
/// either the transpose or the column slice.
let modeProdTDecl (name: string) (dims: int list) (mode: int) (rOut: int) : FunctionDecl =
    let nRank = dims.Length
    let iMode = dims.[mode]
    let outDims = dims |> List.mapi (fun k d -> if k = mode then rOut else d)
    let outStrides = [ for k in 0 .. nRank - 1 -> prodInts (List.skip (k + 1) outDims) ]
    let ovars = [ for k in 0 .. nRank - 1 -> $"o{k}" ]
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

/// (Truncated) HOSVD for a fixed shape: per mode n, U_n = leading R_n eigenvectors (descending eigenvalues) of the mode-n
/// Gram; core = X x0 U0^T x1 U1^T ... -- X ~= core x0 U0 x1 U1 .... Returns (core, U0, ..., U{N-1}); the tuple arity is
/// fixed per rank at elaboration time, rank polymorphism lives in this F# generator.
///
/// The body is pure orchestration: it calls the per-mode generated Gram, eigh, and transposed-mode-product helpers (whose
/// names the elaborator passes in; the eigh helpers are shared with user m.eigh calls of the same shape).
let hosvdDecl (name: string) (dims: int list) (ranks: int list)
              (gramNames: string list) (eighNames: string list) (mptNames: string list)
    : FunctionDecl =
    let nRank = dims.Length
    let stmts =
        [ for mode in 0 .. nRank - 1 do
            yield sLet $"g{mode}" (syn (ExprApp (v gramNames.[mode], [ v "x" ])))
            yield StmtLet { Pattern = synPat (PatTuple [ synPat (PatVar $"q{mode}"); synPat (PatVar $"l{mode}") ])
                            Type = None
                            Value = syn (ExprApp (v eighNames.[mode], [ v $"g{mode}" ]))
                            Mutability = BindLet } ]
        @ [ for mode in 0 .. nRank - 1 ->
              let src = if mode = 0 then "x" else $"c{mode - 1}"
              sLet $"c{mode}" (syn (ExprApp (v mptNames.[mode], [ v src; v $"q{mode}" ]))) ]
        @ [ for mode in 0 .. nRank - 1 ->
              // U_mode = leading ranks[mode] columns of q_mode (literal-index reads; bound to a let, tuple-of-variables boundary)
              sLet $"u{mode}"
                   (syn (ExprArrayLit
                        [ for i in 0 .. dims.[mode] - 1 ->
                            syn (ExprArrayLit
                                [ for r in 0 .. ranks.[mode] - 1 ->
                                    idx2 $"q{mode}" (iLit i) (iLit r) ]) ])) ]
    let retTuple =
        tupleE(v $"c{nRank - 1}" :: [ for mode in 0 .. nRank - 1 -> v $"u{mode}" ])
    let retTy =
        TyTuple (tyFloatTensor ranks :: [ for mode in 0 .. nRank - 1 -> tyFloatMat dims.[mode] ranks.[mode] ])
    mkFunc name [ ("x", tyFloatTensor dims) ] retTy (blockE (stmts, Some retTuple))
