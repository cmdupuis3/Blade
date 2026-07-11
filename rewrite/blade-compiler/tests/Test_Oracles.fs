// Review of tests/Oracles.fs against INDEPENDENTLY VERIFIED truth (plan
// Phase 0.2, the Hermitian-oracle lesson: an oracle nobody checked is just
// a second implementation of the same bug). Every expected value below was
// derived analytically or by hand — never by running the oracle — so these
// tests pin the oracles' conventions as well as their arithmetic:
//
//   - Reynolds oracles are UNNORMALIZED group sums (no 1/r! factor), the
//     convention Blade's reynolds() emits. Consequently the symmetric
//     rank-2 oracle DOUBLES on the diagonal: both elements of S2 map (i,i)
//     to itself, so v(i,i) = 2*g(a_i, a_i). This differs from a plain
//     comm(x,y) triangular iteration (which evaluates g once per canonical
//     tuple, no sum) — the differential harness compares like with like.
//   - Antisym tuples are strict (i0 < i1 < ...), emitted in lexicographic
//     (left-justified DFS) order, matching Blade's compact storage order.
//   - Gram is A * B^H: G[i][j] = sum_t A[i,t] * conj(A[j,t]), upper
//     triangle in row-major (i, j>=i) order.
module Blade.Tests.OracleReview

open Blade.Tests.TestHarness
open Blade.Tests.Oracles

let private close (a: float) (b: float) = abs (a - b) < 1e-9
let private closeL (xs: float list) (ys: float list) =
    xs.Length = ys.Length && List.forall2 close xs ys

let runOracleTests () : BlockResult =
    printHeader "Oracle Review Tests"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    // -- oracleAntisymReynolds ------------------------------------------
    // r=2, g(x,y) = 2x+y: antisymmetrization is (2a+b)-(2b+a) = a-b per
    // strict pair, lexicographic order. For A=[5;2;7]: pairs (0,1),(0,2),
    // (1,2) -> [5-2; 5-7; 2-7] = [3; -2; -5].
    check "antisym r=2: 2x+y antisymmetrizes to x-y"
        (closeL (oracleAntisymReynolds [|5.0; 2.0; 7.0|] 2 (fun v -> 2.0*v.[0] + v.[1]))
                [3.0; -2.0; -5.0]) ""
    // A kernel depending on ONE variable antisymmetrizes to zero for r>=2:
    // each value appears in (r-1)! positive and (r-1)! negative terms.
    check "antisym r=3: degenerate kernel g=x vanishes"
        (closeL (oracleAntisymReynolds [|1.0; 2.0; 4.0; 8.0|] 3 (fun v -> v.[0]))
                [0.0; 0.0; 0.0; 0.0]) ""
    // Vandermonde identity: sum_sigma sgn(sigma) x^0 y^1 z^2 over values
    // (a,b,c) is det[[1,1,1],[a,b,c],[a^2,b^2,c^2]] = (b-a)(c-a)(c-b).
    // For (1,2,4): 1*3*2 = 6.
    check "antisym r=3: Vandermonde determinant identity"
        (closeL (oracleAntisymReynolds [|1.0; 2.0; 4.0|] 3 (fun v -> v.[1] * v.[2] * v.[2]))
                [6.0]) ""
    // Cross-oracle consistency: the general-r oracle at r=2 must agree with
    // the dedicated rank-2 antisym oracle, read out in the same lex order.
    let a4 = [|3.0; 1.0; 4.0; 1.5|]
    let g2 (v: float[]) = v.[0] * v.[0] * v.[1]
    let viaGeneral = oracleAntisymReynolds a4 2 g2
    let viaRank2 =
        [ for i in 0 .. a4.Length - 1 do
            for j in i + 1 .. a4.Length - 1 do
                yield (oracleAntiReynolds2 a4 g2).[(i, j)] ]
    check "antisym: general r=2 agrees with dedicated rank-2 oracle"
        (closeL viaGeneral viaRank2) ""

    // -- oracleGramHermitian --------------------------------------------
    // Hand-computed: A = [[1+i, 2], [3, 4-i]] (m=2, k=2).
    //   G[0][0] = (1+i)(1-i) + 2*2            = 2 + 4        = 6
    //   G[0][1] = (1+i)*conj(3) + 2*conj(4-i) = 3+3i + 8+2i  = 11+5i
    //   G[1][1] = 3*3 + (4-i)(4+i)            = 9 + 17       = 26
    let re = array2D [ [1.0; 2.0]; [3.0; 4.0] ]
    let im = array2D [ [1.0; 0.0]; [0.0; -1.0] ]
    let gram = oracleGramHermitian re im
    let gramExpected = [ (6.0, 0.0); (11.0, 5.0); (26.0, 0.0) ]
    check "gram hermitian: hand-computed 2x2 complex example"
        (gram.Length = 3 &&
         List.forall2 (fun (er, ei) (ar, ai) -> close er ar && close ei ai) gramExpected gram) ""
    // Structural Hermitian property: every diagonal entry is real and
    // equals the row's squared norm (here |1+i|^2 + |2|^2 = 6).
    check "gram hermitian: diagonal is real row-norm^2"
        (match gram with
         | (r0, i0) :: _ -> close i0 0.0 && close r0 6.0
         | [] -> false) ""

    // -- oracleSymReynolds2 / diagonal convention -------------------------
    // Unnormalized S2 sum: off-diagonal v = g(ai,aj) + g(aj,ai); diagonal
    // v = 2*g(ai,ai) (both permutations hit the same point). For g = x*y,
    // A=[2;3]: (0,0)->8, (0,1)->12, (1,1)->18.
    let symM = oracleSymReynolds2 [|2.0; 3.0|] (fun v -> v.[0] * v.[1])
    check "sym r=2: unnormalized sum, diagonal doubles"
        (close symM.[(0,0)] 8.0 && close symM.[(0,1)] 12.0 && close symM.[(1,1)] 18.0) ""
    // Asymmetric kernel: g(x,y) = 2x+y symmetrizes to 3(x+y)/... exactly
    // g(a,b)+g(b,a) = 3(a+b). For (0,1) over [2;3]: 15.
    check "sym r=2: asymmetric kernel symmetrizes to 3(x+y)"
        (close (oracleSymReynolds2 [|2.0; 3.0|] (fun v -> 2.0*v.[0] + v.[1])).[(0,1)] 15.0) ""

    // -- fact / binomIncl / exactSimplexRatio -----------------------------
    check "fact: 0!,1!,5!" (close (fact 0) 1.0 && close (fact 1) 1.0 && close (fact 5) 120.0) ""
    // binomIncl n r = C(n+r-1, r): known values (the same numbers the
    // Buffer Type block pins for device cardinality): C(6,2)=15, C(6,3)=20,
    // C(5,2)=10 is the STRICT count so NOT binomIncl — use C(4+3-1,3)=20 etc.
    check "binomIncl: C(5+2-1,2)=15, C(4+3-1,3)=20, C(3+4-1,4)=15"
        (close (binomIncl 5 2) 15.0 && close (binomIncl 4 3) 20.0 && close (binomIncl 3 4) 15.0) ""
    // Independent formula: C(n+r-1, r) = (n+r-1)! / (r! (n-1)!).
    let binomViaFact n r = fact (n + r - 1) / (fact r * fact (n - 1))
    check "binomIncl: agrees with factorial formula (n<=8, r<=4)"
        ([ for n in 1 .. 8 do for r in 0 .. 4 -> close (binomIncl n r) (binomViaFact n r) ]
         |> List.forall id) ""
    // exactSimplexRatio: r=2 one axis of extent 2 -> 2^2 / C(3,2) = 4/3;
    // approaches r! = 2 from below as the extent grows; d axes multiply.
    check "exactSimplexRatio: 4/3 at n=2; monotone toward 2; product over axes"
        (close (exactSimplexRatio 2 [2]) (4.0/3.0)
         && exactSimplexRatio 2 [1000] > 1.99 && exactSimplexRatio 2 [1000] < 2.0
         && close (exactSimplexRatio 2 [2; 2]) (16.0/9.0)) ""

    printFooter "Oracle Review" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "Oracle Review"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
