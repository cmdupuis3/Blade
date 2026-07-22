/// The Cartesian<->irreps bridge constants (rank-2, 3-D, v1) — the single
/// compiled source of truth for the rep-INTRODUCTION ops
/// `ml.tensor_to_irreps` / `ml.sym_to_irreps` / `ml.irreps_to_sym` and for
/// the sgs field formers that share the pack order.
///
/// FROZEN CONVENTIONS (certified against the SphericalHarmonics fit by
/// ml/ `dump-cartesian`; corpus rotation/parity certificates sgs/001-003):
///  - 3x3 Cartesian tensors flatten row-major: g[3i+j] = G_ij (for a
///    velocity gradient, G_ij = d_j u_i).
///  - Symmetric pack order: [(0,0); (0,1); (0,2); (1,1); (1,2); (2,2)]
///    (upper triangle, row-major) — `packPairs` below IS the definition.
///  - gradSpec = [(0,e,1); (1,e,1); (2,e,1)] (dim 9): trace, then the
///    AXIAL pseudovector (vorticity) in Y1 component order (y, z, x) with
///    a_x = g21 - g12, then the symmetric-traceless part in Y2 order
///    (xy, yz, 3z^2-r^2, xz, x^2-y^2). A rank-2 Cartesian tensor is
///    odd (x) odd = parity-EVEN throughout — the l=1 block does NOT flip
///    under improper elements (the dump-cartesian improper certificate
///    detects the wrong (1, odd) assignment at O(1)).
///  - tauSpec = [(0,e,1); (2,e,1)] (dim 6) for symmetric tensors.
///  - All rows are ORTHONORMAL over R^9 with the Frobenius inner product:
///    |G|_F = |bridge9 G|; the packed-6 forms weight off-diagonals by
///    sqrt(2), and irrToSym is the exact inverse of symToIrr.
///  - The l=2 rows relate to the y_to solid-harmonic constants by the one
///    Schur ratio sqrt(15/8pi) (pinned in tests/Test_CartesianBridge.fs).
module Blade.ML.CartesianBridge

open Blade.ML.Spec

/// The irreps decomposition of a full 3x3 Cartesian tensor.
let gradSpec : Spec =
    [ { L = 0; Parity = 0; Mult = 1 }
      { L = 1; Parity = 0; Mult = 1 }
      { L = 2; Parity = 0; Mult = 1 } ]

/// The irreps decomposition of a symmetric 3x3 Cartesian tensor.
let tauSpec : Spec =
    [ { L = 0; Parity = 0; Mult = 1 }
      { L = 2; Parity = 0; Mult = 1 } ]

/// THE symmetric pack order (upper triangle, row-major).
let packPairs : (int * int) list = [ (0, 0); (0, 1); (0, 2); (1, 1); (1, 2); (2, 2) ]

let private s2 = sqrt 2.0
let private s3 = sqrt 3.0
let private s6 = sqrt 6.0

let private row9 (entries: (int * int * float) list) : float list =
    let r = Array.zeroCreate 9
    for (i, j, c) in entries do r.[3 * i + j] <- c
    List.ofArray r

/// 9x9 orthonormal bridge, rows in irreps order (l=0 | l=1 | l=2), acting
/// on the flat row-major 3x3.
let bridge9Rows : float list list =
    [ row9 [ (0, 0, 1.0 / s3); (1, 1, 1.0 / s3); (2, 2, 1.0 / s3) ]        // l=0 trace
      row9 [ (0, 2, 1.0 / s2); (2, 0, -1.0 / s2) ]                          // l=1 a_y = (g02-g20)/sqrt2
      row9 [ (1, 0, 1.0 / s2); (0, 1, -1.0 / s2) ]                          // l=1 a_z = (g10-g01)/sqrt2
      row9 [ (2, 1, 1.0 / s2); (1, 2, -1.0 / s2) ]                          // l=1 a_x = (g21-g12)/sqrt2
      row9 [ (0, 1, 1.0 / s2); (1, 0, 1.0 / s2) ]                           // l=2 xy
      row9 [ (1, 2, 1.0 / s2); (2, 1, 1.0 / s2) ]                           // l=2 yz
      row9 [ (0, 0, -1.0 / s6); (1, 1, -1.0 / s6); (2, 2, 2.0 / s6) ]       // l=2 3z^2-r^2
      row9 [ (0, 2, 1.0 / s2); (2, 0, 1.0 / s2) ]                           // l=2 xz
      row9 [ (0, 0, 1.0 / s2); (1, 1, -1.0 / s2) ] ]                        // l=2 x^2-y^2

/// 6x6 forward bridge for packed symmetric tensors (Frobenius weighting:
/// off-diagonal packed entries carry sqrt(2)).
let symToIrrRows : float list list =
    [ [ 1.0 / s3; 0.0; 0.0; 1.0 / s3; 0.0; 1.0 / s3 ]
      [ 0.0; s2; 0.0; 0.0; 0.0; 0.0 ]
      [ 0.0; 0.0; 0.0; 0.0; s2; 0.0 ]
      [ -1.0 / s6; 0.0; 0.0; -1.0 / s6; 0.0; 2.0 / s6 ]
      [ 0.0; 0.0; s2; 0.0; 0.0; 0.0 ]
      [ 1.0 / s2; 0.0; 0.0; -1.0 / s2; 0.0; 0.0 ] ]

/// 6x6 exact inverse of symToIrrRows (rows = packed cells s00..s22).
let irrToSymRows : float list list =
    [ [ 1.0 / s3; 0.0; 0.0; -1.0 / s6; 0.0; 1.0 / s2 ]    // s00
      [ 0.0; 1.0 / s2; 0.0; 0.0; 0.0; 0.0 ]                // s01
      [ 0.0; 0.0; 0.0; 0.0; 1.0 / s2; 0.0 ]                // s02
      [ 1.0 / s3; 0.0; 0.0; -1.0 / s6; 0.0; -1.0 / s2 ]    // s11
      [ 0.0; 0.0; 1.0 / s2; 0.0; 0.0; 0.0 ]                // s12
      [ 1.0 / s3; 0.0; 0.0; 2.0 / s6; 0.0; 0.0 ] ]         // s22

let bridge9Flat : float list = List.concat bridge9Rows
let symToIrrFlat : float list = List.concat symToIrrRows
let irrToSymFlat : float list = List.concat irrToSymRows
