// Blade Reynolds term-plan core
//
// Shared, rendering-independent computation of the Reynolds permutation
// "term plan": which permutations of the kernel parameters survive
// deduplication, with what integer coefficients, and in what order they are
// summed. Extracted verbatim from CodeGen.fs so a future IR interpreter can
// reuse the EXACT term enumeration / dedup / ordering (observable in the float
// bits of test output). CodeGen renders each term to C++; an interpreter would
// instead evaluate the kernel under each surviving permutation.
//
// The canonical key (commutative-op normalization) is supplied by the caller
// as `buildKey`, because that key is built from CodeGen's C++-rendering helper
// (floatToCppLiteral) and so stays where the rendering logic lives. Everything
// here is pure integer/list/string logic and depends on no IR/Types module.
module Blade.ReynoldsCore

/// Generate all permutations of a list of integers
let rec permutations (items: int list) : int list list =
    match items with
    | [] -> [[]]
    | _ ->
        items |> List.collect (fun x ->
            let rest = items |> List.filter (fun i -> i <> x)
            permutations rest |> List.map (fun p -> x :: p))

/// Count inversions to get permutation sign (+1 for even, -1 for odd)
let permSign (perm: int list) : int =
    let mutable inv = 0
    for i in 0 .. perm.Length - 2 do
        for j in i + 1 .. perm.Length - 1 do
            if perm.[i] > perm.[j] then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

/// The Reynolds term plan: the deduplicated, coefficient-weighted permutation
/// terms in first-occurrence order, plus the pre-dedup permutation count.
type ReynoldsTermPlan = {
    /// (coeff, representativePerm) for each surviving term, in first-occurrence
    /// order. representativePerm is the first permutation (in enumeration order)
    /// carrying that term's canonical key; the caller renders/evaluates the
    /// kernel under it.
    Terms: (int * int list) list
    /// Total permutation count before dedup (n!), for the dedup statistics.
    TotalPerms: int
}

/// Enumerate permutations of [0..n-1], group them by canonical key (produced by
/// the caller-supplied `buildKey`, which normalizes commutative ops), and dedup
/// into integer-coefficient terms in first-occurrence order. This is exactly the
/// enumeration/dedup/ordering that genKernelExprWithReynolds used to perform
/// inline; the caller renders each surviving (coeff, perm) term.
let reynoldsTermPlan (n: int) (isAntisymmetric: bool) (buildKey: int list -> string) : ReynoldsTermPlan =
    let allPerms = permutations [0 .. n - 1]
    let totalPerms = allPerms.Length
    // For each permutation, generate:
    //   - canonical key (for grouping -- commutative ops normalized)
    //   - sign
    // and carry the permutation itself (for actual emission by the caller).
    let permData =
        allPerms |> List.map (fun perm ->
            let sign = permSign perm
            let key = buildKey perm
            (key, sign, perm))
    // Group by canonical key to deduplicate equivalent permutations.
    // For symmetric Reynolds: identical keys accumulate multiplicity.
    // For antisymmetric Reynolds: identical keys accumulate net sign (may cancel to 0).
    let terms =
        permData
        |> List.groupBy (fun (key, _, _) -> key)
        |> List.choose (fun (_key, group) ->
            let representativePerm = let (_, _, perm) = group.Head in perm
            if isAntisymmetric then
                let netSign = group |> List.sumBy (fun (_, s, _) -> s)
                if netSign = 0 then None
                else Some (netSign, representativePerm)
            else
                Some (group.Length, representativePerm))
    { Terms = terms; TotalPerms = totalPerms }
