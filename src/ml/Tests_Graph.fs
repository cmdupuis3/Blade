namespace BladeML

/// Message passing and the end-to-end equivariant convolution
/// (the ml-spec section 12 complete example).
module Tests_Graph =

    open System
    open TestHarness
    open MathUtils
    open Irreps

    let private randArray (rng: Random) (n: int) : float[] =
        Array.init n (fun _ -> rng.NextDouble() * 2.0 - 1.0)

    let run () =
        section "message passing: gather / scatter_add"

        // 3 nodes, feature dim 2.
        let features = [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0 |]
        let gathered = MessagePassing.gather features 2 3 [| 2; 0; 0; 1 |]
        checkArrayClose "gather picks source rows" 0.0
            [| 5.0; 6.0; 1.0; 2.0; 1.0; 2.0; 3.0; 4.0 |] gathered

        let values = [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0; 7.0; 8.0 |]
        let scattered = MessagePassing.scatterAdd values 2 [| 1; 0; 1; 1 |] 3
        checkArrayClose "scatter_add accumulates at targets" 0.0
            [| 3.0; 4.0; 13.0; 16.0; 0.0; 0.0 |] scattered

        checkThrows "gather rejects out-of-range source" (fun () ->
            MessagePassing.gather features 2 3 [| 3 |] |> ignore)
        checkThrows "scatter_add rejects out-of-range target" (fun () ->
            MessagePassing.scatterAdd values 2 [| 1; 0; 3; 1 |] 3 |> ignore)

        // gather then scatter_add along the same index list = per-row
        // multiplication by in-degree counts.
        let g2 = MessagePassing.gather features 2 3 [| 0; 0; 2 |]
        let s2 = MessagePassing.scatterAdd g2 2 [| 0; 0; 2 |] 3
        checkArrayClose "scatter(gather(x)) = degree-weighted x" 1e-15
            [| 2.0; 4.0; 0.0; 0.0; 5.0; 6.0 |] s2

        section "equivariant convolution: end-to-end"

        let specIn = mkSpec [ (0, Even, 3); (1, Odd, 2) ]
        let specOut = mkSpec [ (0, Even, 4); (1, Odd, 3); (2, Even, 2) ]
        let lmax = 2
        let cfg = { Spec1 = specIn; Spec2 = shSpec lmax; SpecOut = specOut }
        let dIn = totalDim specIn
        let dOut = totalDim specOut

        let nNodes = 5
        let rng = Random(31337)
        let edgeSrc = [| 0; 1; 2; 3; 4; 1; 3; 0 |]
        let edgeTgt = [| 1; 2; 3; 4; 0; 0; 1; 2 |]
        let nEdges = edgeSrc.Length
        let edgeVecs = randArray rng (nEdges * 3)
        let nodeFeat = randArray rng (nNodes * dIn)
        let weights = randArray rng (TensorProduct.weightDim cfg)

        let out = Conv.equivariantConv specIn specOut lmax nodeFeat nNodes edgeSrc edgeTgt edgeVecs weights
        check "conv output shape" (out.Length = nNodes * dOut)

        // A node with no incoming edges stays zero: rewire so node 4 gets nothing.
        let tgtNo4 = edgeTgt |> Array.map (fun t -> if t = 4 then 0 else t)
        let outNo4 = Conv.equivariantConv specIn specOut lmax nodeFeat nNodes edgeSrc tgtNo4 edgeVecs weights
        check "node with no incoming edges is zero"
            (Array.sub outNo4 (4 * dOut) dOut |> Array.forall (fun t -> t = 0.0))

        // Equivariance: rotate node features by the rep and edge vectors by
        // R; the output must be the rep-rotated original output.
        let r = Rotations.randomRotation rng
        let nodeFeatRot =
            Array.init nNodes (fun n ->
                Rotations.applyRep specIn r (Array.sub nodeFeat (n * dIn) dIn))
            |> Array.concat
        let edgeVecsRot =
            Array.init nEdges (fun e ->
                matVec r (Array.sub edgeVecs (e * 3) 3))
            |> Array.concat
        let outRotInputs =
            Conv.equivariantConv specIn specOut lmax nodeFeatRot nNodes edgeSrc edgeTgt edgeVecsRot weights
        let outThenRot =
            Array.init nNodes (fun n ->
                Rotations.applyRep specOut r (Array.sub out (n * dOut) dOut))
            |> Array.concat
        checkArrayClose "conv equivariance: conv(D_in x, R v) = D_out conv(x, v)" 1e-6
            outThenRot outRotInputs

        // Edge relabeling invariance (scatter_add is order-insensitive up to
        // floating-point reassociation).
        let perm = [| 5; 2; 7; 0; 4; 6; 1; 3 |]
        let permuted =
            Conv.equivariantConv specIn specOut lmax nodeFeat nNodes
                (perm |> Array.map (fun i -> edgeSrc.[i]))
                (perm |> Array.map (fun i -> edgeTgt.[i]))
                (perm |> Array.collect (fun i -> Array.sub edgeVecs (i * 3) 3))
                weights
        checkArrayClose "conv invariant under edge relabeling" 1e-12 out permuted

        // Linearity in node features: conv(x + x') = conv(x) + conv(x').
        let nodeFeat2 = randArray rng (nNodes * dIn)
        let outSum =
            Conv.equivariantConv specIn specOut lmax
                (Array.map2 (+) nodeFeat nodeFeat2) nNodes edgeSrc edgeTgt edgeVecs weights
        let out2 = Conv.equivariantConv specIn specOut lmax nodeFeat2 nNodes edgeSrc edgeTgt edgeVecs weights
        checkArrayClose "conv linear in node features" 1e-10 (Array.map2 (+) out out2) outSum
