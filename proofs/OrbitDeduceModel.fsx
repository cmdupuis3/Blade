// OrbitDeduceModel.fsx — AST-robustness stress tests for OrbIdx type deduction.
// Companion to docs/plan-orbit-index-types.md (§7 deduction rule, §9.4) and to
// OrbitEnum.fsx (which verifies the class calculus itself). This file verifies
// the calculus THROUGH the AST: each case states the target deduction rule for
// one Blade construct, names the compiler seam it must land in, and validates
// the rule by brute-force stabilizer enumeration over generic tensors.
//
// Constructs covered (per the 2026-08-01 combinator inventory):
//   [*] outer product        — BinOpMode.Outer, mkOuterResult (TypeCheck.fs:5357)
//   *   elementwise          — zip desugar (TypeCheck.fs:5015-5041)
//   <@> kernel apply         — buildApplyInfo/deduceOutputType (TypeCheck.fs:6061, IR.fs:2028)
//   <$> / unary map          — OpFunctor (TypeCheck.fs:4801) + rank-0 kernels (IR.fs:2160-2179)
//   <|> choice               — OpChoice (TypeCheck.fs:4857)
//   reduce                   — inferReduce (TypeCheck.fs:3357; compact storage is an error today)
//   reynolds                 — wrapper owns symmetry (IR.fs:2119-2123)
//
// The brute side measures the PLAIN value stabilizer: permutations sigma with
// T[x . sigma] = T[x] for all x — i.e. ker(chi), the sign-free part of the
// licensed group. A class with any '-' level of rank >= 2 has |ker| = |G|/2.
//
// Run: dotnet fsi proofs/OrbitDeduceModel.fsx      (exit 1 on any failure)

#load "OrbitEnum.fsx"
open OrbitEnum
open System.Collections.Generic

// ---------- generic-value machinery ----------
// Values are signed ints; 0 marks a zero-set cell. Distinct tags never collide.
let mutable idCtr = 100
let memo = Dictionary<string, int>()
let genVal (tag: string) (key: string) =
    let k = tag + "|" + key
    match memo.TryGetValue k with
    | true, v -> v
    | _ ->
        idCtr <- idCtr + 1
        memo.[k] <- idCtr
        idCtr

/// Leaf tensor for one class: canonicalize, sign the generic cell id.
let leafValue (tag: string) (levels: (int * Sign) list) (tup: int list) : int =
    match canon levels tup with
    | None -> 0
    | Some (c, s) -> s * genVal tag (c |> List.map string |> String.concat ",")

/// Generic commutative pairing (fresh id per unordered multiset of values).
let commKernel (tag: string) (vs: int list) : int =
    genVal tag (vs |> List.sort |> List.map string |> String.concat ",")

// Unary maps by parity class.
let mapOdd v = 3 * v                                   // odd, injective
let mapEven v = abs v                                  // even: g(-v) = g(v), g(0) = 0
let mapGeneral v = genVal "gen" (string v)             // no sign relation; g(0) <> 0

// ---------- brute-force plain stabilizer ----------
let stabCount (rank: int) (n: int) (tensor: int[] -> int) : int =
    let total = pown n rank
    let digits = Array.init total (fun e -> Array.init rank (fun a -> (e / pown n a) % n))
    let pw = Array.init rank (fun a -> pown n a)
    let T = Array.init total (fun e -> tensor digits.[e])
    let mutable count = 0
    for sigma in allPerms rank do
        let mutable ok = true
        let mutable e = 0
        while ok && e < total do
            let d = digits.[e]
            let mutable pe = 0
            for a in 0 .. rank - 1 do
                pe <- pe + d.[sigma.[a]] * pw.[a]
            if T.[e] <> T.[pe] then ok <- false
            e <- e + 1
        if ok then count <- count + 1
    count

let digitsOf (n: int) (rank: int) (d: int[]) : int list = List.init rank (fun a -> int d.[a])

// ---------- the model calculus (target deduction rules) ----------
// A type is a juxtaposition of classes; each class is a level list.
type Ty = (int * Sign) list list

let hasMinus (c: (int * Sign) list) = c |> List.exists (fun (r, s) -> s = Minus && r >= 2)
let rankOfCls (c: (int * Sign) list) = c |> List.fold (fun a (r, _) -> a * r) 1

/// ker(chi) size of the deduced type — what the plain brute stabilizer must equal
/// when the deduction is exact.
let expectedStab (t: Ty) : int64 =
    t
    |> List.map (fun c ->
        let c = normalize c
        if c.IsEmpty then 1L
        else
            let g = groupOrder (c |> List.map fst)
            if hasMinus c then g / 2L else g)
    |> List.fold (*) 1L

/// Unary map rule (seam: OpFunctor TypeCheck.fs:4801-4815 and the rank-0 kernel
/// arm of deduceOutputType, IR.fs:2160-2179 — both today preserve the class
/// VERBATIM for any kernel, which is unsound for non-odd kernels over '-' levels).
type Parity = POdd | PEven | PGeneral
let mapCls (p: Parity) (c: (int * Sign) list) : Ty =
    match p with
    | POdd -> [ c ]                                          // signs survive
    | PEven -> [ c |> List.map (fun (r, _) -> (r, Plus)) ]   // g(-v)=g(v): '-' flips to '+'
    | PGeneral ->
        if hasMinus c then List.replicate (rankOfCls c) []   // round down: even part inexpressible
        else [ c ]                                           // all-'+' classes survive ANY map

/// Choice rule (seam: OpChoice TypeCheck.fs:4857-4861): pointwise selection keeps
/// only symmetry common to both operands — identical classes survive, else free.
let meetCls (a: (int * Sign) list) (b: (int * Sign) list) : Ty =
    if normalize a = normalize b then [ normalize a ] else List.replicate (rankOfCls a) []

// ---------- report ----------
let mutable failures = 0
let check (name: string) (expected: 'a) (actual: 'a) =
    let ok = (expected = actual)
    if not ok then failures <- failures + 1
    printfn "%s  %s: expected %A, got %A" (if ok then "PASS" else "FAIL") name expected actual

// =====================================================================
// T1. Leaf sanity: plain stabilizer = ker(chi) of the declared class.
//     The [(3,-)] case is the exhibit: ker(sgn) = A_3, order 3 — a group
//     with NO OrbIdx spelling (not a Young/wreath subgroup).
// =====================================================================
let leafStab levels n =
    stabCount (rankOfCls levels) n (fun d -> leafValue "L" levels (digitsOf n (rankOfCls levels) d))
check "T1a leaf [(2,+)] n=3" 2 (leafStab [ (2, Plus) ] 3)
check "T1b leaf [(2,-)] n=3" 1 (leafStab [ (2, Minus) ] 3)
check "T1c leaf [(3,-)] n=3 (A_3!)" 3 (leafStab [ (3, Minus) ] 3)
check "T1d leaf Riemann [(2,-),(2,+)] n=3" 4 (leafStab [ (2, Minus); (2, Plus) ] 3)
check "T1e expectedStab agrees (Riemann)" 4L (expectedStab [ [ (2, Minus); (2, Plus) ] ])

// =====================================================================
// T2/T3. [*] outer product (seam: mkOuterResult, TypeCheck.fs:5357-5365).
//     Today: concatenates IndexTypes and WIPES Identity=None — so even a
//     let-bound P [*] P cannot tie downstream. Target rule: same identity
//     + comm outer op appends a level; distinct operands juxtapose.
// =====================================================================
let A2 d = leafValue "A" [ (2, Plus) ] d
let B2 d = leafValue "B" [ (2, Plus) ] d
let n2 = 2
let pTensor (d: int list) = commKernel "f" [ A2 [ d.[0]; d.[1] ]; A2 [ d.[2]; d.[3] ] ]
check "T2 let P=f(A,A) in P [*] P -> depth 3" 128 (stabCount 8 n2 (fun d ->
    let d = digitsOf n2 8 d
    commKernel "outer" [ pTensor [ d.[0]; d.[1]; d.[2]; d.[3] ]; pTensor [ d.[4]; d.[5]; d.[6]; d.[7] ] ]))
check "T2b expectedStab depth-3" 128L (expectedStab [ [ (2, Plus); (2, Plus); (2, Plus) ] ])
let qTensor (d: int list) = commKernel "g" [ B2 [ d.[0]; d.[1] ]; B2 [ d.[2]; d.[3] ] ]
check "T3 f(A,A) [*] g(B,B) -> untied 64" 64 (stabCount 8 n2 (fun d ->
    let d = digitsOf n2 8 d
    commKernel "outer2" [ pTensor [ d.[0]; d.[1]; d.[2]; d.[3] ]; qTensor [ d.[4]; d.[5]; d.[6]; d.[7] ] ]))

// =====================================================================
// T4-T8. Unary maps (seams: OpFunctor <$> and rank-0 elementwise kernels).
//     '+' levels are robust under ANY map; '-' levels survive only odd maps,
//     flip to '+' under even maps, and under a general map the value group
//     degrades to ker(chi) — which OrbIdx cannot spell, so the deduction
//     rounds down AND must diagnose (a BL4015-family "residual group not
//     representable" warning; doc §8 — closing the family means parity-split
//     chiral classes, deliberately deferred). The verbatim-preservation
//     unsoundness these cases originally exposed is now gated by BL4015
//     (corpus 163-170).
// =====================================================================
let riemann d = leafValue "R" [ (2, Minus); (2, Plus) ] d
check "T4 odd map over Riemann: signs survive" 4 (stabCount 4 3 (fun d ->
    mapOdd (riemann (digitsOf 3 4 d))))
check "T5a even map over [(2,-)]: flips to [(2,+)]" 2 (stabCount 2 3 (fun d ->
    mapEven (leafValue "U" [ (2, Minus) ] (digitsOf 3 2 d))))
check "T5b even map over Riemann -> [(2,+),(2,+)]" 8 (stabCount 4 3 (fun d ->
    mapEven (riemann (digitsOf 3 4 d))))
check "T6 general map over tied [(2,+),(2,+)]: robust" 8 (stabCount 4 3 (fun d ->
    mapGeneral (pTensor (digitsOf 3 4 d))))
check "T7 general map over [(3,-)]: true group is A_3" 3 (stabCount 3 3 (fun d ->
    mapGeneral (leafValue "V" [ (3, Minus) ] (digitsOf 3 3 d))))
check "T7b model rounds A_3 down to trivial (sound)" 1L (expectedStab (mapCls PGeneral [ (3, Minus) ]))
check "T8 general map over Riemann: true group order 4" 4 (stabCount 4 3 (fun d ->
    mapGeneral (riemann (digitsOf 3 4 d))))
check "T8b model rounds it to trivial (sound, 4x loss)" 1L (expectedStab (mapCls PGeneral [ (2, Minus); (2, Plus) ]))

// =====================================================================
// T9. Elementwise * of an array with itself (seam: the zip desugar,
//     TypeCheck.fs:5015-5041, which routes through co-iteration and
//     hard-codes SCNeither). X*X is an EVEN map of X: antisym input
//     yields a symmetric result. Today: dense (sound, lossy).
// =====================================================================
check "T9 X*X elementwise, X antisym -> [(2,+)]" 2 (stabCount 2 3 (fun d ->
    let v = leafValue "W" [ (2, Minus) ] (digitsOf 3 2 d)
    v * v))

// =====================================================================
// T10. <|> choice (seam: OpChoice): only common symmetry survives.
// =====================================================================
check "T10a choice(antisym, sym) -> trivial" 1 (stabCount 2 3 (fun d ->
    let t = digitsOf 3 2 d
    let u = leafValue "U2" [ (2, Minus) ] t
    if u <> 0 then u else leafValue "S2" [ (2, Plus) ] t))
check "T10b choice(sym, sym') keeps [(2,+)]" 2 (stabCount 2 3 (fun d ->
    let t = digitsOf 3 2 d
    let u = leafValue "S3" [ (2, Plus) ] t
    if u <> 0 then u else leafValue "S4" [ (2, Plus) ] t))
check "T10c meet rule agrees" 1L (expectedStab (meetCls [ (2, Minus) ] [ (2, Plus) ]))

// =====================================================================
// T11-T13. Reductions (seam: inferReduce — today ANY reduce over compact
//     storage is a hard error, TypeCheck.fs:3531-3542/3586-3587, and the
//     user is steered to decompact-first). Target residual rules:
//     - puncturing one axis of one block breaks every level above it;
//     - reducing ALIGNED axes (same position in every tied block) lowers
//       the inner rank and KEEPS the tie;
//     - reducing a full block lowers the outer tie rank.
// =====================================================================
let tied4 d = pTensor d   // generic wreath-tied rank-4, n=3
// Reductions must combine ORDER-INDEPENDENTLY and collision-free: fingerprint
// the multiset of summed terms (a plain int sum could collide by accident).
let msum (terms: int list) : int = genVal "red" (terms |> List.sort |> List.map string |> String.concat ",")
check "T11 reduce axis 0 of tied depth-2 -> [] (+) [(2,+)]" 2 (stabCount 3 3 (fun d ->
    let t = digitsOf 3 3 d
    msum [ for i in 0 .. 2 -> tied4 [ i; t.[0]; t.[1]; t.[2] ] ]))
check "T12 reduce aligned {0,2} -> tie survives: [(2,+)]" 2 (stabCount 2 3 (fun d ->
    let t = digitsOf 3 2 d
    msum [ for i in 0 .. 2 do for k in 0 .. 2 -> tied4 [ i; t.[0]; k; t.[1] ] ]))
check "T13 reduce full block {0,1} -> [(2,+)]" 2 (stabCount 2 3 (fun d ->
    let t = digitsOf 3 2 d
    msum [ for i in 0 .. 2 do for j in 0 .. 2 -> tied4 [ i; j; t.[0]; t.[1] ] ]))

// =====================================================================
// T14. Tie chains survive postcomposition: general map over the depth-2
//     tied class (all '+') keeps the full wreath. Seam: <$> after <@>.
// =====================================================================
check "T14 general map over f(A,A): still 8" 8 (stabCount 4 3 (fun d ->
    mapGeneral (pTensor (digitsOf 3 4 d))))

// =====================================================================
// T15. Multiset partial tie regression (seam: buildApplyInfo, ternary):
//     h(C,C,D) -> [(2,+),(2,+)] (+) [(2,+)], plain stab 16.
// =====================================================================
check "T15 h(C,C,D) partial tie" 16 (stabCount 6 3 (fun d ->
    let t = digitsOf 3 6 d
    commKernel "h" [ leafValue "C" [ (2, Plus) ] [ t.[0]; t.[1] ]
                     leafValue "C" [ (2, Plus) ] [ t.[2]; t.[3] ]
                     leafValue "D" [ (2, Plus) ] [ t.[4]; t.[5] ] ]))

// =====================================================================
// T16. Reynolds rule (model-only; seam: IR.fs:2119-2123): the wrapper owns
//     the output symmetry — a reynolds-wrapped kernel's comm is an
//     iteration license and must append NO level (doc §8.1).
// =====================================================================
let reynoldsDeduce (_inputCls: (int * Sign) list) (wrapperSign: Sign) : Ty =
    [ [ (2, wrapperSign) ] ]   // the wrapper's own claim; kernel comm adds nothing
check "T16 reynolds appends no kernel-comm level" [ [ (2, Minus) ] ] (reynoldsDeduce [ (2, Plus) ] Minus)

printfn ""
if failures = 0 then
    printfn "DEDUCE MODEL: ALL PASS"
    exit 0
else
    printfn "DEDUCE MODEL: %d FAILURE(S)" failures
    exit 1
