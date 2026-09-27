/// sgs-module decl builders: the field formers of the subgrid-closure arc
/// (central-difference gradient, box filter, exact subgrid stress)
/// synthesized as plain Blade source.
///
/// Field convention (component-first, space-last -- the spatial axes are the
/// sample axes of the stress comoment):
///   U : Array<Float64 like Idx<3>, Idx<n>, Idx<n>, Idx<n>>   (cubic, periodic)
///
/// House style follows Blade.Math.Decls (whose AST helpers this module
/// reuses): flat rank-1 `mut` work arrays with static strides, for-in
/// nests, rank-N outputs assembled as nested literals of runtime reads.
/// Every generated body MIRRORS the sgs/ oracle (Oracle.fs /
/// TrainingOracle.fs) term for term and in accumulation order, so corpus
/// pins agree to the ulp. The symmetric pack order is the ONE definition in
/// Blade.ML.CartesianBridge.packPairs.
module Blade.Sgs.Decls

open Blade.Ast
open Blade.Math.Decls

let private modE a b = syn (ExprBinOp (Elementwise, OpMod, a, b))
let private idxN (arr: string) (args: Expr list) = syn (ExprApp (v arr, args))

let private packPairs = Blade.ML.CartesianBridge.packPairs |> List.toArray

/// sgs.grad for a fixed cubic extent n: G(c, d, i, j, k) = d_d u_c at
/// (i, j, k) -- 2nd-order central differences, periodic wrap, spacing dx:
///   (u(c, i+1, j, k) - u(c, i-1, j, k)) / (2 dx)   (axis d = 0; etc.)
/// Mirrors the sgs/ oracle's fdGrad expression exactly.
let gradDecl (name: string) (n: int) : FunctionDecl =
    let n3 = n * n * n
    let nn = n * n
    // flat offset into the (3, 3, n, n, n) work array
    let gCell (cE: Expr) (d: int) (iE: Expr) (jE: Expr) (kE: Expr) =
        add (mul cE (iLit (3 * n3)))
            (add (iLit (d * n3))
                 (add (mul iE (iLit nn)) (add (mul jE (iLit n)) kE)))
    let uAt (cE: Expr) (iE: Expr) (jE: Expr) (kE: Expr) = idxN "u" [ cE; iE; jE; kE ]
    let twoDx = mul (fLit 2.0) (v "dx")
    let body =
        blockE (
            [ sLetMut "g" (zerosLit (9 * n3))
              sFor "c" 0 3
                [ sFor "i" 0 n
                    [ sFor "j" 0 n
                        [ sFor "k" 0 n
                            [ sLet "ip" (modE (add (v "i") (iLit 1)) (iLit n))
                              sLet "im" (modE (add (v "i") (iLit (n - 1))) (iLit n))
                              sLet "jp" (modE (add (v "j") (iLit 1)) (iLit n))
                              sLet "jm" (modE (add (v "j") (iLit (n - 1))) (iLit n))
                              sLet "kp" (modE (add (v "k") (iLit 1)) (iLit n))
                              sLet "km" (modE (add (v "k") (iLit (n - 1))) (iLit n))
                              sAssign (idx "g" (gCell (v "c") 0 (v "i") (v "j") (v "k")))
                                      (divE (sub (uAt (v "c") (v "ip") (v "j") (v "k"))
                                                 (uAt (v "c") (v "im") (v "j") (v "k"))) twoDx)
                              sAssign (idx "g" (gCell (v "c") 1 (v "i") (v "j") (v "k")))
                                      (divE (sub (uAt (v "c") (v "i") (v "jp") (v "k"))
                                                 (uAt (v "c") (v "i") (v "jm") (v "k"))) twoDx)
                              sAssign (idx "g" (gCell (v "c") 2 (v "i") (v "j") (v "k")))
                                      (divE (sub (uAt (v "c") (v "i") (v "j") (v "kp"))
                                                 (uAt (v "c") (v "i") (v "j") (v "km"))) twoDx) ] ] ] ] ],
            Some (nestedFromFlatN "g" [ 3; 3; n; n; n ] 0))
    mkFunc name
        [ ("u", tyFloatTensor [ 3; n; n; n ]); ("dx", tyFloat) ]
        (tyFloatTensor [ 3; 3; n; n; n ]) body

/// sgs.box_filter for fixed (n, w), m = n/w: per-component tile means.
/// Accumulate then divide into a SEPARATE buffer (norms' grad-safe shape:
/// no read-then-rewrite). In-tile accumulation order (di, dj, dk) ascending
/// mirrors the oracle's sample order t = di*w^2 + dj*w + dk.
let boxFilterDecl (name: string) (n: int) (w: int) : FunctionDecl =
    let m = n / w
    let m3 = m * m * m
    let mm = m * m
    let cell (cE: Expr) =
        add (mul cE (iLit m3))
            (add (mul (v "ti") (iLit mm)) (add (mul (v "tj") (iLit m)) (v "tk")))
    let fine (tE: Expr) (dE: Expr) = add (mul tE (iLit w)) dE
    let body =
        blockE (
            [ sLetMut "acc" (zerosLit (3 * m3))
              sLetMut "out" (zerosLit (3 * m3))
              sFor "c" 0 3
                [ sFor "ti" 0 m
                    [ sFor "tj" 0 m
                        [ sFor "tk" 0 m
                            [ sFor "di" 0 w
                                [ sFor "dj" 0 w
                                    [ sFor "dk" 0 w
                                        [ sAccum (idx "acc" (cell (v "c")))
                                                 (idxN "u" [ v "c";
                                                             fine (v "ti") (v "di");
                                                             fine (v "tj") (v "dj");
                                                             fine (v "tk") (v "dk") ]) ] ] ] ] ] ] ]
              sFor "p" 0 (3 * m3)
                [ sAssign (idx "out" (v "p")) (divE (idx "acc" (v "p")) (fLit (float (w * w * w)))) ] ],
            Some (nestedFromFlatN "out" [ 3; m; m; m ] 0))
    mkFunc name [ ("u", tyFloatTensor [ 3; n; n; n ]) ] (tyFloatTensor [ 3; m; m; m ]) body

/// sgs.stress for fixed (n, w), m = n/w, T = w^3: the EXACT subgrid stress
/// per coarse cell -- the second central comoment of velocity under the
/// filter-cell measure, packed in CartesianBridge.packPairs order:
///   tau_p(cell) = (1/T) Sum_t y_a(t) y_b(t) - (s_a/T)(s_b/T),
///   y_c(t) = u_c(t) - mu_c(cell),  mu_c = (1/T) Sum_t u_c(t),  s_c = Sum_t y_c(t)
/// TWO PASSES over each tile: the tile means first, then the centered
/// products (s_c is the rounding residue of the mean, ~0, kept so the formula
/// is exact algebra). The one-pass raw form prodsum/T - mu_a*mu_b it replaces
/// cancels catastrophically when the mean flow dwarfs the fluctuation -- the
/// regime a subgrid stress lives in -- and made the stress's Galilean
/// invariance hold only to the rounding of the raw moments; centered, a boost
/// moves tau only through the rounding of the centered values themselves.
/// The sgs/ oracle computes the same quantity exactly (oracles/sgs/Exact.fs).
let stressDecl (name: string) (n: int) (w: int) : FunctionDecl =
    let m = n / w
    let m3 = m * m * m
    let mm = m * m
    let tF = float (w * w * w)
    let cellBase = add (mul (v "ti") (iLit mm)) (add (mul (v "tj") (iLit m)) (v "tk"))
    let fine (tE: Expr) (dE: Expr) = add (mul tE (iLit w)) dE
    let uc (c: int) =
        idxN "u" [ iLit c; fine (v "ti") (v "di"); fine (v "tj") (v "dj"); fine (v "tk") (v "dk") ]
    let meanSweep =
        [ for c in 0 .. 2 -> sAccum (idx "sm" (add (iLit (c * m3)) cellBase)) (uc c) ]
    let centeredSweep =
        [ yield sLet "x0" (sub (uc 0) (idx "mu" (add (iLit 0) cellBase)))
          yield sLet "x1" (sub (uc 1) (idx "mu" (add (iLit m3) cellBase)))
          yield sLet "x2" (sub (uc 2) (idx "mu" (add (iLit (2 * m3)) cellBase)))
          yield sAccum (idx "s1" (add (iLit 0) cellBase)) (v "x0")
          yield sAccum (idx "s1" (add (iLit m3) cellBase)) (v "x1")
          yield sAccum (idx "s1" (add (iLit (2 * m3)) cellBase)) (v "x2")
          yield! (packPairs |> Array.toList |> List.mapi (fun p (a, b) ->
                      sAccum (idx "pp" (add (iLit (p * m3)) cellBase))
                             (mul (v $"x{a}") (v $"x{b}")))) ]
    let assemble =
        packPairs |> Array.toList |> List.mapi (fun p (a, b) ->
            sAssign (idx "out" (add (iLit (p * m3)) cellBase))
                    (sub (divE (idx "pp" (add (iLit (p * m3)) cellBase)) (fLit tF))
                         (mul (divE (idx "s1" (add (iLit (a * m3)) cellBase)) (fLit tF))
                              (divE (idx "s1" (add (iLit (b * m3)) cellBase)) (fLit tF)))))
    let tileLoops (body: Stmt list) =
        sFor "ti" 0 m
            [ sFor "tj" 0 m
                [ sFor "tk" 0 m
                    [ sFor "di" 0 w
                        [ sFor "dj" 0 w
                            [ sFor "dk" 0 w body ] ] ] ] ]
    let body =
        blockE (
            [ sLetMut "sm" (zerosLit (3 * m3))
              sLetMut "mu" (zerosLit (3 * m3))
              sLetMut "s1" (zerosLit (3 * m3))
              sLetMut "pp" (zerosLit (6 * m3))
              sLetMut "out" (zerosLit (6 * m3))
              tileLoops meanSweep
              // the tile means into a SEPARATE buffer (accumulate-then-divide,
              // no read-then-rewrite -- the box filter's grad-safe shape)
              sFor "p" 0 (3 * m3) [ sAssign (idx "mu" (v "p")) (divE (idx "sm" (v "p")) (fLit tF)) ]
              tileLoops centeredSweep
              sFor "ti" 0 m
                [ sFor "tj" 0 m
                    [ sFor "tk" 0 m assemble ] ] ],
            Some (nestedFromFlatN "out" [ 6; m; m; m ] 0))
    mkFunc name [ ("u", tyFloatTensor [ 3; n; n; n ]) ] (tyFloatTensor [ 6; m; m; m ]) body
