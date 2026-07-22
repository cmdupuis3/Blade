// gw_build.fsx â€” oracle + generator for examples/physics/47 (Golubâ€“Welsch /
// flat extension). (a) exact 3-atom recovery from 6 moments (Chebyshev
// recursion -> Jacobi tridiagonal -> eigh: nodes + weights); (b) flatness =
// rank stall (Hankel H3 singular, H2 not); (c) damped double-well attractors:
// the tower counts (rank), locates (nodes Â±1), weighs (basin fractions =
// direct counts); (d) the Tsirelson tower refused (negative Hankel eigenvalue).

open System
open System.Text

// ---------- (a) 3-atom truth ----------
let atoms = [| -1.1; 0.4; 1.7 |]
let wts = [| 0.2; 0.5; 0.3 |]
let mom k = Array.map2 (fun a w -> w * a ** float k) atoms wts |> Array.sum
let m = Array.init 7 (fun k -> mom k)
printfn "moments: %s" (m |> Array.map (sprintf "%.10f") |> String.concat " ")

// Chebyshev -> Jacobi coefficients (n = 3)
let a0 = m.[1] / m.[0]
let s1 = Array.init 5 (fun l -> if l >= 1 then m.[l + 1] - a0 * m.[l] else 0.0)
let b1 = s1.[1] / m.[0]
let a1 = s1.[2] / s1.[1] - a0
let s2 = Array.init 4 (fun l -> if l >= 2 then s1.[l + 1] - a1 * s1.[l] - b1 * m.[l] else 0.0)
let b2 = s2.[2] / s1.[1]
let a2 = s2.[3] / s2.[2] - s1.[2] / s1.[1]
printfn "jacobi: a = (%.10f, %.10f, %.10f)  b = (%.10f, %.10f)" a0 a1 a2 b1 b2

let eigh (A0: float[,]) =
    let n = Array2D.length1 A0
    let A = Array2D.copy A0
    let V = Array2D.init n n (fun i j -> if i = j then 1.0 else 0.0)
    let offNorm () =
        let mutable s = 0.0
        for i in 0 .. n - 1 do
            for j in i + 1 .. n - 1 do s <- s + A.[i, j] * A.[i, j]
        s
    let mutable sweep = 0
    while offNorm () > 1e-30 && sweep < 200 do
        sweep <- sweep + 1
        for p in 0 .. n - 2 do
            for q in p + 1 .. n - 1 do
                if abs A.[p, q] > 1e-24 then
                    let theta = (A.[q, q] - A.[p, p]) / (2.0 * A.[p, q])
                    let t = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (theta * theta + 1.0))
                    let c = 1.0 / sqrt (t * t + 1.0)
                    let sn = t * c
                    for k in 0 .. n - 1 do
                        let akp = A.[k, p]
                        let akq = A.[k, q]
                        A.[k, p] <- c * akp - sn * akq
                        A.[k, q] <- sn * akp + c * akq
                    for k in 0 .. n - 1 do
                        let apk = A.[p, k]
                        let aqk = A.[q, k]
                        A.[p, k] <- c * apk - sn * aqk
                        A.[q, k] <- sn * apk + c * aqk
                    for k in 0 .. n - 1 do
                        let vkp = V.[k, p]
                        let vkq = V.[k, q]
                        V.[k, p] <- c * vkp - sn * vkq
                        V.[k, q] <- sn * vkp + c * vkq
    let idx = [| 0 .. n - 1 |] |> Array.sortByDescending (fun i -> A.[i, i])
    (idx |> Array.map (fun i -> A.[i, i]), Array2D.init n n (fun r c -> V.[r, idx.[c]]))

let J3 = array2D [ [ a0; sqrt b1; 0.0 ]; [ sqrt b1; a1; sqrt b2 ]; [ 0.0; sqrt b2; a2 ] ]
let (nodes, qj) = eigh J3
printfn "nodes (desc): %s  (truth 1.7, 0.4, -1.1)" (nodes |> Array.map (sprintf "%.12f") |> String.concat " ")
printfn "weights: %s (truth 0.3, 0.5, 0.2)" ([| 0..2 |] |> Array.map (fun k -> sprintf "%.12f" (m.[0] * qj.[0, k] * qj.[0, k])) |> String.concat " ")

// (b) flatness: H3 (4x4, m0..m6) vs H2 (3x3, m0..m4)
let H3 = Array2D.init 4 4 (fun i j -> m.[i + j])
let H2 = Array2D.init 3 3 (fun i j -> m.[i + j])
let (l3, _) = eigh H3
let (l2, _) = eigh H2
printfn "H3 eigs: %s (min ~0: rank stalls at 3)" (l3 |> Array.map (sprintf "%.3e") |> String.concat " ")
printfn "H2 eigs: %s (min > 0)" (l2 |> Array.map (sprintf "%.3e") |> String.concat " ")

// ---------- (c) damped double-well attractor ensemble ----------
let gamma = 0.25
let h = 0.01
let nsteps = 6000
let finals =
    Array.init 24 (fun j ->
        let mutable x = -2.1 + 4.6 * float j / 23.0
        let mutable p = 0.0
        for _ in 1 .. nsteps do
            let f0 = x - x * x * x - 2.0 * gamma * p
            // velocity Verlet with velocity-dependent force: half-kick estimate
            let ph = p + 0.5 * h * f0
            x <- x + h * ph
            let f1 = x - x * x * x - 2.0 * gamma * ph
            p <- ph + 0.5 * h * f1
        x)
printfn "finals: %s" (finals |> Array.map (sprintf "%+.4f") |> String.concat " ")
let nRight = finals |> Array.filter (fun x -> x > 0.0) |> Array.length
printfn "direct counts: right %d / 24" nRight
let em = Array.init 5 (fun k -> (finals |> Array.sumBy (fun x -> x ** float k)) / 24.0)
let ea0 = em.[1]
let es1 = Array.init 4 (fun l -> if l >= 1 then em.[l + 1] - ea0 * em.[l] else 0.0)
let eb1 = es1.[1]
let ea1 = es1.[2] / es1.[1] - ea0
let J2 = array2D [ [ ea0; sqrt eb1 ]; [ sqrt eb1; ea1 ] ]
let (anodes, aq) = eigh J2
printfn "attractor nodes: %s  weights: %s" (anodes |> Array.map (sprintf "%.10f") |> String.concat " ") ([| 0; 1 |] |> Array.map (fun k -> sprintf "%.10f" (aq.[0, k] * aq.[0, k])) |> String.concat " ")
let EH2 = Array2D.init 3 3 (fun i j -> em.[i + j])
let (el2, _) = eigh EH2
printfn "attractor H2 eigs: %s (min ~0: rank stalls at 2)" (el2 |> Array.map (sprintf "%.3e") |> String.concat " ")

// ---------- generate blade ----------
let sb = StringBuilder()
let w (s: string) = sb.AppendLine(s) |> ignore
let header = [
  "// TEST: Physics -- The Tower Counts, Locates, and Weighs: Flat Extension"
  "// ============================================================================"
  "// The flat-extension guard (Curto-Fialkow), prototyped in userland -- the"
  "// compiler slot the arc has pointed at since ex 33 ('extendable' as a"
  "// refinement type on Dist). The question a truncated tower must answer:"
  "// is there a MEASURE behind these numbers? For atomic measures the whole"
  "// answer is spectral, and with m.eigh it runs in-language end to end:"
  "// moments -> Chebyshev recursion -> Jacobi tridiagonal -> eigh ="
  "// Golub-Welsch: eigenvalues are the ATOMS, first-row eigenvector squares"
  "// are the WEIGHTS, and Hankel rank certifies the count."
  "// FINDINGS (pinned):"
  "//   * SIX MOMENTS ARE THE MEASURE. From m0..m6 of a 3-atom law the"
  "//     machine returns the atoms (-1.1, 0.4, 1.7) and weights (0.2, 0.5,"
  "//     0.3) to ~1e-13 -- and FLATNESS certifies exactness: the 4x4 Hankel's"
  "//     smallest eigenvalue is ~1e-16 (rank stalls at 3) while the 3x3's"
  "//     is finite. Rank stall = 'the extension is flat' = the tower IS a"
  "//     3-atom measure, no deeper story required (contrast ex 33, where no"
  "//     extension of any depth exists)."
  "//   * THE t -> infinity TOWER IS THE ATTRACTOR PORTRAIT (review-gap 4"
  "//     closed): 24 damped double-well trajectories, integrated in-language"
  "//     to t = 60, leave an ensemble whose moment tower is 2-atomic: rank"
  "//     stalls at 2 (COUNTS the attractors), Golub-Welsch nodes land on"
  "//     the wells at Â±1 to ~1e-7 (LOCATES them -- the residual is the"
  "//     e^{-gamma t} ring-down, physics not error), and the recovered"
  "//     weights equal the DIRECTLY COUNTED basin fractions -- 12/24 each, an"
  "//     even split off an ASYMMETRIC grid (interleaved spiral basins),"; "//     matched at 1e-14 (WEIGHS). Detect -> count -> locate -> weigh,"
  "//     all from one moment tower."
  "//   * THE GUARD REFUSES NONCLASSICAL TOWERS. The Tsirelson B-marginal"
  "//     (m0, m1, m2) = (1, 2 sqrt 2, 4) of ex 33/39: its Hankel has a"
  "//     NEGATIVE eigenvalue (-0.7016) -- no measure, no atoms, refusal by"
  "//     spectrum. The same machine that hands back atoms for classical"
  "//     towers hands back the obstruction for quantum ones: this pair of"
  "//     behaviors IS the flat-extension guard the compiler slot wants."
  "// Two-route: every number verified against an independent F# route"
  "// (Chebyshev + Jacobi eigh) before pinning; the 3-atom case's truth is"
  "// analytic."
  "// ============================================================================"
  "import math as m"
  "" ]
header |> List.iter w

w "// ---- (a) the 3-atom tower: moments in-language, Chebyshev, Jacobi ----"
w "let AT: Array<Float64 like Idx<3>> = [-1.1, 0.4, 1.7]"
w "let WT: Array<Float64 like Idx<3>> = [0.2, 0.5, 0.3]"
w "let MM = {"
w "    let mut mv = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
w "    let mut pw = [0.0, 0.0, 0.0]"
w "    for i in 0..3 { pw(i) = WT(i) }"
w "    for k in 0..7 {"
w "        let s = pw(0) + pw(1) + pw(2)"
w "        mv(k) = s"
w "        for i in 0..3 { pw(i) = pw(i) * AT(i) }"
w "    }"
w "    mv"
w "}"
w "let ja0 = MM(1) / MM(0)"
w "let s11 = MM(2) - ja0 * MM(1)"
w "let s12 = MM(3) - ja0 * MM(2)"
w "let s13 = MM(4) - ja0 * MM(3)"
w "let s14 = MM(5) - ja0 * MM(4)"
w "let jb1 = s11 / MM(0)"
w "let ja1 = s12 / s11 - ja0"
w "let s22 = s13 - ja1 * s12 - jb1 * MM(2)"
w "let s23 = s14 - ja1 * s13 - jb1 * MM(3)"
w "let jb2 = s22 / s11"
w "let ja2 = s23 / s22 - s12 / s11"
w "let JM: Array<Float64 like Idx<3>, Idx<3>> = [[ja0, sqrt(jb1), 0.0], [sqrt(jb1), ja1, sqrt(jb2)], [0.0, sqrt(jb2), ja2]]"
w "let (QJ, ND) = m.eigh(JM)"
w "let nd0 = ND(0)"
w "let nd1 = ND(1)"
w "let nd2 = ND(2)"
w "let w0 = MM(0) * QJ(0, 0) * QJ(0, 0)"
w "let w1 = MM(0) * QJ(0, 1) * QJ(0, 1)"
w "let w2 = MM(0) * QJ(0, 2) * QJ(0, 2)"
w "let atomDev = sqrt((nd0 - 1.7) * (nd0 - 1.7)) + sqrt((nd1 - 0.4) * (nd1 - 0.4)) + sqrt((nd2 + 1.1) * (nd2 + 1.1)) + sqrt((w0 - 0.3) * (w0 - 0.3)) + sqrt((w1 - 0.5) * (w1 - 0.5)) + sqrt((w2 - 0.2) * (w2 - 0.2))"
w ""
w "// ---- (b) flatness: rank stalls exactly at the atom count ----"
w "let H3: Array<Float64 like Idx<4>, Idx<4>> = [[MM(0), MM(1), MM(2), MM(3)], [MM(1), MM(2), MM(3), MM(4)], [MM(2), MM(3), MM(4), MM(5)], [MM(3), MM(4), MM(5), MM(6)]]"
w "let H2: Array<Float64 like Idx<3>, Idx<3>> = [[MM(0), MM(1), MM(2)], [MM(1), MM(2), MM(3)], [MM(2), MM(3), MM(4)]]"
w "let (QH3, L3) = m.eigh(H3)"
w "let (QH2, L2) = m.eigh(H2)"
w "let flat3 = sqrt(L3(3) * L3(3))"
w "let firm2 = L2(2)"
w ""
w "// ---- (c) the attractor portrait: damped double-well, in-language ----"
w "let IC24 = method_for(range<Idx<24>>) <@> lambda(j) -> 0.0 - 2.1 + 4.6 * j / 23.0 |> compute"
w "let FIN = {"
w "    let mut fv = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
w "    let mut x = 0.0"
w "    let mut p = 0.0"
w "    for j in 0..24 {"
w "        x = IC24(j)"
w "        p = 0.0"
w "        for t in 0..6000 {"
w "            let f0 = x - x * x * x - 0.5 * p"
w "            let ph = p + 0.005 * f0"
w "            x = x + 0.01 * ph"
w "            let f1 = x - x * x * x - 0.5 * ph"
w "            p = ph + 0.005 * f1"
w "        }"
w "        fv(j) = x"
w "    }"
w "    fv"
w "}"
w "let em0 = 1.0"
w "let em1 = reduce(method_for(FIN) <@> lambda(x) -> x, (+)) / 24.0"
w "let em2 = reduce(method_for(FIN) <@> lambda(x) -> x * x, (+)) / 24.0"
w "let em3 = reduce(method_for(FIN) <@> lambda(x) -> x * x * x, (+)) / 24.0"
w "let em4 = reduce(method_for(FIN) <@> lambda(x) -> x * x * x * x, (+)) / 24.0"
w "let nRight = reduce(method_for(FIN) <@> lambda(x) -> (if x > 0.0 then 1.0 else 0.0), (+))"
w "let ea0 = em1"
w "let t11 = em2 - ea0 * em1"
w "let t12 = em3 - ea0 * em2"
w "let eb1 = t11"
w "let ea1 = t12 / t11 - ea0"
w "let J2M: Array<Float64 like Idx<2>, Idx<2>> = [[ea0, sqrt(eb1)], [sqrt(eb1), ea1]]"
w "let (QA, NA) = m.eigh(J2M)"
w "let an0 = NA(0)"
w "let an1 = NA(1)"
w "let aw0 = QA(0, 0) * QA(0, 0)"
w "let aw1 = QA(0, 1) * QA(0, 1)"
w "let wellDev = sqrt((an0 - 1.0) * (an0 - 1.0)) + sqrt((an1 + 1.0) * (an1 + 1.0))"
w "let countDev = sqrt((aw0 - nRight / 24.0) * (aw0 - nRight / 24.0))"
w "let EH2: Array<Float64 like Idx<3>, Idx<3>> = [[1.0, em1, em2], [em1, em2, em3], [em2, em3, em4]]"
w "let (QE, LE) = m.eigh(EH2)"
w "let aflat = sqrt(LE(2) * LE(2))"
w "let afirm = (if LE(1) > 0.0 then LE(1) else 0.0 - LE(1))"
w ""
w "// ---- (d) the guard refuses the Tsirelson tower ----"
w "let TS: Array<Float64 like Idx<2>, Idx<2>> = [[1.0, 2.82842712474619], [2.82842712474619, 4.0]]"
w "let (QT, LT) = m.eigh(TS)"
w "let tneg = LT(1)"
w ""
w "// ---- verdicts ----"
w "let atomsV = if atomDev < 0.000000000001 then \"SIX_MOMENTS_ARE_THE_MEASURE\" else \"NO\""
w "let flatV = if flat3 < 0.00000000001 then (if firm2 > 0.01 then \"RANK_STALLS_AT_THE_ATOM_COUNT\" else \"NO\") else \"NO\""
w "let wellsV = if wellDev < 0.00001 then \"WELLS_LOCATED\" else \"NO\""
w "let basinV = if countDev < 0.00000001 then \"WEIGHTS_ARE_BASIN_FRACTIONS\" else \"NO\""
w "let aflatV = if aflat < 0.0000000001 then (if afirm > 0.5 then \"ATTRACTORS_COUNTED\" else \"NO\") else \"NO\""
w "let guardV = if tneg < 0.0 then \"TSIRELSON_REFUSED_NEGATIVE_EIGENVALUE\" else \"NO\""
w ""
w "// atoms out of moments; rank = count, nodes = places, weights = basins;"
w "// and on the quantum tower, the spectral refusal:"
let pins = [
  "nd0 = 1.7"
  "nd1 = 0.400000000000001"
  "nd2 = -1.1"
  "w0 = 0.3"
  "w1 = 0.5"
  "w2 = 0.2"
  "atomDev = 1.2490009027033e-15"
  "atomsV = \"SIX_MOMENTS_ARE_THE_MEASURE\""
  "flat3 = 5.7728687742232e-17"
  "firm2 = 0.361014849269617"
  "flatV = \"RANK_STALLS_AT_THE_ATOM_COUNT\""
  "nRight = 12"
  "an0 = 0.999999937492779"
  "an1 = -0.999999973120443"
  "aw0 = 0.500000000000014"
  "aw1 = 0.499999999999986"
  "wellDev = 8.93867779794277e-08"
  "countDev = 1.3988810110277e-14"
  "aflat = 1.0492907946876e-13"
  "afirm = 0.999999910613223"
  "wellsV = \"WELLS_LOCATED\""
  "basinV = \"WEIGHTS_ARE_BASIN_FRACTIONS\""
  "aflatV = \"ATTRACTORS_COUNTED\""
  "tneg = -0.701562118716424"
  "guardV = \"TSIRELSON_REFUSED_NEGATIVE_EIGENVALUE\"" ]
pins |> List.iter (fun p -> w ("// EXPECT: " + p))

IO.File.WriteAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "47_flat_extension.blade"), sb.ToString())
printfn "blade written"
