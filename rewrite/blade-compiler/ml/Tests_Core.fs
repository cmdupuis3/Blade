namespace BladeML

/// Irreps, specs, and the IrrepsIdx index-type contract
/// (domain / cardinality / bijection / lex enumeration).
module Tests_Core =

    open TestHarness
    open Irreps

    let private spec60 = mkSpec [ (0, Even, 16); (1, Odd, 8); (2, Even, 4) ]

    let run () =
        section "core: parity and irreps"

        // Z_2 group laws for parity_mul.
        check "parity: Even is identity" (parityMul Even Odd = Odd && parityMul Even Even = Even)
        check "parity: Odd self-inverse" (parityMul Odd Odd = Even)
        check "parity: commutative" (parityMul Odd Even = parityMul Even Odd)
        let assoc =
            [ Even; Odd ] |> List.forall (fun a ->
                [ Even; Odd ] |> List.forall (fun b ->
                    [ Even; Odd ] |> List.forall (fun c ->
                        parityMul a (parityMul b c) = parityMul (parityMul a b) c)))
        check "parity: associative" assoc

        check "dim L0e = 1" (dim L0e = 1)
        check "dim L1o = 3" (dim L1o = 3)
        check "dim L2e = 5" (dim L2e = 5)
        checkThrows "negative L rejected" (fun () -> irrep -1 Even |> ignore)
        checkThrows "zero multiplicity rejected" (fun () -> mkSpec [ (0, Even, 0) ] |> ignore)

        // The ml-spec's worked example: 16x0e + 8x1o + 4x2e = 60.
        check "totalDim example spec = 60" (totalDim spec60 = 60)
        check "blockDim (L1o, 8) = 24" (blockDim { Ir = L1o; Mult = 8 } = 24)

        // sh_spec: alternating parity, mult 1, totalDim (lmax+1)^2.
        let sh3 = shSpec 3
        check "shSpec 3 length" (sh3.Length = 4)
        check "shSpec 3 parities alternate"
            (sh3.[0].Ir = L0e && sh3.[1].Ir = L1o && sh3.[2].Ir = L2e && sh3.[3].Ir = L3o)
        check "shSpec 3 totalDim = 16" (totalDim sh3 = 16)
        check "shSpec mults all 1" (sh3 |> Array.forall (fun e -> e.Mult = 1))

        section "core: IrrepsIdx contract"

        // Cardinality.
        check "extent = totalDim" (IrrepsIdx.extent spec60 = 60)
        let starts = IrrepsIdx.blockStarts spec60
        check "blockStarts = [0;16;40;60]"
            (starts.[0] = 0 && starts.[1] = 16 && starts.[2] = 40 && starts.[3] = 60)

        // Enumeration: exactly cardinality entries, in lex order, and the
        // storage bijection maps enumeration position i to offset i.
        let triples = IrrepsIdx.enumerate spec60
        check "enumerate length = extent" (triples.Length = 60)
        let lexOrdered =
            triples
            |> Array.pairwise
            |> Array.forall (fun ((b1, mu1, m1), (b2, mu2, m2)) ->
                (b1, mu1, m1) < (b2, mu2, m2))
        check "enumeration is strictly lexicographic" lexOrdered
        let offsetsSequential =
            triples |> Array.mapi (fun i t -> IrrepsIdx.offsetOf spec60 t = i) |> Array.forall id
        check "offsetOf(enumerate[i]) = i (enumeration = offset order)" offsetsSequential
        let roundTrip =
            [ 0 .. 59 ] |> List.forall (fun off ->
                IrrepsIdx.offsetOf spec60 (IrrepsIdx.ofOffset spec60 off) = off)
        check "ofOffset/offsetOf round-trip on all offsets" roundTrip

        // Spot values: last element of the last block.
        check "offsetOf (2,3,4) = 59" (IrrepsIdx.offsetOf spec60 (2, 3, 4) = 59)
        check "ofOffset 59 = (2,3,4)" (IrrepsIdx.ofOffset spec60 59 = (2, 3, 4))
        check "offsetOf (1,0,0) = 16" (IrrepsIdx.offsetOf spec60 (1, 0, 0) = 16)

        // Domain violations are errors, not wraparound.
        checkThrows "offsetOf rejects bad block" (fun () -> IrrepsIdx.offsetOf spec60 (3, 0, 0) |> ignore)
        checkThrows "offsetOf rejects bad mult" (fun () -> IrrepsIdx.offsetOf spec60 (0, 16, 0) |> ignore)
        checkThrows "offsetOf rejects bad m" (fun () -> IrrepsIdx.offsetOf spec60 (1, 0, 3) |> ignore)
        checkThrows "ofOffset rejects extent" (fun () -> IrrepsIdx.ofOffset spec60 60 |> ignore)
