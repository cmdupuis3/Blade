namespace BladeML

/// Tensor product, equivariant linear layer, and activations.
/// The central checks are numerical EQUIVARIANCE: rotate the inputs with the
/// fitted Wigner D representation, compare against the rotated output.
module Tests_Ops =

    open System
    open TestHarness
    open MathUtils
    open Irreps

    let private randArray (rng: Random) (n: int) : float[] =
        Array.init n (fun _ -> rng.NextDouble() * 2.0 - 1.0)

    /// Reorder helper for the cross-product check: cartesian (x,y,z) ->
    /// real-SH degree-1 component order (y, z, x).
    let private toShOrder (v: float[]) : float[] = [| v.[1]; v.[2]; v.[0] |]

    let private cross (a: float[]) (b: float[]) : float[] =
        [| a.[1] * b.[2] - a.[2] * b.[1]
           a.[2] * b.[0] - a.[0] * b.[2]
           a.[0] * b.[1] - a.[1] * b.[0] |]

    let run () =
        section "tensor product: paths and weights (ml-spec section 12 config)"

        // The complete-example config from ml-spec section 12.
        let specIn = mkSpec [ (0, Even, 16); (1, Odd, 8) ]
        let specSh = shSpec 2
        let specOut = mkSpec [ (0, Even, 32); (1, Odd, 16); (2, Even, 8) ]
        let cfg = { Spec1 = specIn; Spec2 = specSh; SpecOut = specOut }

        let ps = TensorProduct.paths cfg
        check (sprintf "example config has 7 paths (got %d)" ps.Length) (ps.Length = 7)
        // Hand-enumerated: 0e*0e->0e, 0e*1o->1o, 0e*2e->2e, 1o*0e->1o,
        //                  1o*1o->0e, 1o*1o->2e, 1o*2e->1o.
        let expectedPaths =
            [| { B1 = 0; B2 = 0; BOut = 0 }
               { B1 = 0; B2 = 1; BOut = 1 }
               { B1 = 0; B2 = 2; BOut = 2 }
               { B1 = 1; B2 = 0; BOut = 1 }
               { B1 = 1; B2 = 1; BOut = 0 }
               { B1 = 1; B2 = 1; BOut = 2 }
               { B1 = 1; B2 = 2; BOut = 1 } |]
        check "path enumeration matches hand enumeration" (ps = expectedPaths)
        // Selection rules hold on every path.
        let rulesOk =
            ps |> Array.forall (fun p ->
                let i1 = cfg.Spec1.[p.B1].Ir
                let i2 = cfg.Spec2.[p.B2].Ir
                let io = cfg.SpecOut.[p.BOut].Ir
                io.L >= abs (i1.L - i2.L) && io.L <= i1.L + i2.L
                && io.P = parityMul i1.P i2.P)
        check "all paths satisfy CG selection rules" rulesOk
        check (sprintf "weightDim = 1472 (got %d)" (TensorProduct.weightDim cfg))
            (TensorProduct.weightDim cfg = 1472)
        check "allValidOutputs true for example config" (TensorProduct.allValidOutputs cfg)

        // Unreachable output irrep: L1e cannot come from {0e,1o} x {0e}.
        let badCfg =
            { Spec1 = specIn
              Spec2 = mkSpec [ (0, Even, 1) ]
              SpecOut = mkSpec [ (1, Even, 2) ] }
        check "allValidOutputs false for unreachable output" (not (TensorProduct.allValidOutputs badCfg))
        checkThrows "tensorProduct rejects invalid config (spec 11.1)" (fun () ->
            TensorProduct.tensorProduct badCfg
                (Array.zeroCreate (TensorProduct.weightDim badCfg))
                (Array.zeroCreate (totalDim specIn))
                (Array.zeroCreate 1)
            |> ignore)

        section "tensor product: sparse vs dense differential"

        let rng = Random(2024)
        let w = randArray rng (TensorProduct.weightDim cfg)
        let x = randArray rng (totalDim specIn)
        let y = randArray rng (totalDim specSh)
        let outSparse = TensorProduct.tensorProduct cfg w x y
        let outDense = TensorProduct.tensorProductDense cfg w x y
        checkArrayClose "sparse CG iteration = dense reference" 1e-10 outDense outSparse
        check "output length = totalDim specOut" (outSparse.Length = totalDim specOut)

        section "tensor product: equivariance under SO(3)"

        // Smaller config so the Wigner-D fits stay cheap.
        let eCfg =
            { Spec1 = mkSpec [ (0, Even, 2); (1, Odd, 1) ]
              Spec2 = shSpec 2
              SpecOut = mkSpec [ (0, Even, 2); (1, Odd, 2); (2, Even, 1) ] }
        for trial in 1 .. 3 do
            let w = randArray rng (TensorProduct.weightDim eCfg)
            let x = randArray rng (totalDim eCfg.Spec1)
            let y = randArray rng (totalDim eCfg.Spec2)
            let r = Rotations.randomRotation rng
            let outThenRot =
                Rotations.applyRep eCfg.SpecOut r (TensorProduct.tensorProduct eCfg w x y)
            let rotThenOut =
                TensorProduct.tensorProduct eCfg w
                    (Rotations.applyRep eCfg.Spec1 r x)
                    (Rotations.applyRep eCfg.Spec2 r y)
            checkArrayClose (sprintf "TP equivariance trial %d" trial) 1e-7 outThenRot rotThenOut

        section "tensor product: 1x1->1 is the cross product"

        let xCfg =
            { Spec1 = mkSpec [ (1, Even, 1) ]
              Spec2 = mkSpec [ (1, Even, 1) ]
              SpecOut = mkSpec [ (1, Even, 1) ] }
        check "cross config has exactly 1 path" ((TensorProduct.paths xCfg).Length = 1)
        let wOne = [| 1.0 |]
        let a = randArray rng 3   // cartesian
        let b = randArray rng 3
        let outAB = TensorProduct.tensorProduct xCfg wOne (toShOrder a) (toShOrder b)
        let outBA = TensorProduct.tensorProduct xCfg wOne (toShOrder b) (toShOrder a)
        checkArrayClose "1x1->1 antisymmetric: TP(a,b) = -TP(b,a)" 1e-12
            (outBA |> Array.map (fun t -> -t)) outAB
        // Proportional to the cross product, consistently across pairs.
        let shCross = toShOrder (cross a b)
        let alpha = (Array.map2 (*) outAB shCross |> Array.sum)
                    / (shCross |> Array.sumBy (fun t -> t * t))
        checkArrayClose "1x1->1 proportional to cross(a,b)" 1e-10
            (shCross |> Array.map (fun t -> alpha * t)) outAB
        let c = randArray rng 3
        let d = randArray rng 3
        let outCD = TensorProduct.tensorProduct xCfg wOne (toShOrder c) (toShOrder d)
        checkArrayClose "same proportionality constant on a second pair" 1e-10
            (toShOrder (cross c d) |> Array.map (fun t -> alpha * t)) outCD
        // Self-TP through an antisymmetric path vanishes (ml-spec 14.2's
        // "CG antisym -> 0" row): x (x) x -> 0 for the 1x1->1 path.
        let selfOut = TensorProduct.tensorProduct xCfg wOne (toShOrder a) (toShOrder a)
        check (sprintf "self-TP through antisymmetric path vanishes (max %.2g)" (maxAbs selfOut))
            (maxAbs selfOut < 1e-12)

        section "equivariant linear layer"

        let lSpecIn = mkSpec [ (0, Even, 4); (1, Odd, 3); (2, Even, 2) ]
        let lSpecOut = mkSpec [ (0, Even, 2); (1, Odd, 5); (2, Even, 2) ]
        check "allIrrepsPresent true" (Linear.allIrrepsPresent lSpecIn lSpecOut)
        check "allIrrepsPresent false when output irrep missing"
            (not (Linear.allIrrepsPresent (mkSpec [ (0, Even, 4) ]) lSpecOut))
        check (sprintf "linear weightDim = 27 (got %d)" (Linear.weightDim lSpecIn lSpecOut))
            (Linear.weightDim lSpecIn lSpecOut = 27)

        let lw = randArray rng 27
        let lx = randArray rng (totalDim lSpecIn)
        let lOut = Linear.linear lSpecIn lSpecOut lw lx

        // Equivariance: linear acts on multiplicities, D acts on m — commute.
        let r = Rotations.randomRotation rng
        checkArrayClose "linear equivariance" 1e-8
            (Rotations.applyRep lSpecOut r lOut)
            (Linear.linear lSpecIn lSpecOut lw (Rotations.applyRep lSpecIn r lx))

        // Manual matmul check on the L1o block (out block 1, in block 1).
        let offs = Linear.weightOffsets lSpecIn lSpecOut
        let sIn = IrrepsIdx.blockStarts lSpecIn
        let sOut = IrrepsIdx.blockStarts lSpecOut
        let mutable manualOk = true
        for muO in 0 .. 4 do
            for cc in 0 .. 2 do
                let mutable acc = 0.0
                for muI in 0 .. 2 do
                    acc <- acc + lw.[offs.[1] + muO * 3 + muI] * lx.[sIn.[1] + muI * 3 + cc]
                if abs (acc - lOut.[sOut.[1] + muO * 3 + cc]) > 1e-12 then manualOk <- false
        check "linear = per-block multiplicity matmul shared across m" manualOk

        // Block isolation: weights nonzero only for the L1o block leave all
        // other output blocks at exactly zero.
        let isoW = Array.zeroCreate 27
        for i in offs.[1] .. offs.[2] - 1 do
            isoW.[i] <- rng.NextDouble() + 0.5
        let isoOut = Linear.linear lSpecIn lSpecOut isoW lx
        let scalarsZero = Array.sub isoOut 0 2 |> Array.forall (fun t -> t = 0.0)
        let l2Zero = Array.sub isoOut (sOut.[2]) 10 |> Array.forall (fun t -> t = 0.0)
        check "linear never mixes across irrep blocks" (scalarsZero && l2Zero)

        section "derive_linear reference: complete Schur basis (homLinear)"

        // On duplicate-free all-present specs the pair layout coincides with
        // linear's first-match layout — the two routes must agree exactly.
        checkArrayClose "homLinear = linear on all-present specs" 1e-14
            (Linear.linear lSpecIn lSpecOut lw lx)
            (Linear.homLinear lSpecIn lSpecOut lw lx)

        // Duplicate input irrep (both copies reachable — the F3 fix) and an
        // unmatched output block (zero-filled): equivariance still holds.
        let hIn = mkSpec [ (0, Even, 2); (1, Odd, 1); (1, Odd, 2) ]
        let hOut = mkSpec [ (1, Odd, 2); (2, Even, 1); (0, Even, 1) ]
        check "homWeightDim = homDim (aggregated Schur count)"
            (Linear.homWeightDim hIn hOut = Irreps.homDim hIn hOut)
        let hw = randArray rng (Linear.homWeightDim hIn hOut)
        let hx = randArray rng (totalDim hIn)
        let hOutV = Linear.homLinear hIn hOut hw hx
        let rH = Rotations.randomRotation rng
        checkArrayClose "homLinear equivariance (dup inputs, zero-fill)" 1e-8
            (Rotations.applyRep hOut rH hOutV)
            (Linear.homLinear hIn hOut hw (Rotations.applyRep hIn rH hx))
        let hStarts = IrrepsIdx.blockStarts hOut
        check "homLinear zero-fills the unmatched output block"
            ([ 0 .. 4 ] |> List.forall (fun c -> hOutV.[hStarts.[1] + c] = 0.0))
        // Second input copy of 1o genuinely reaches the output: zero the
        // first copy's weights, keep the second's — output must be nonzero.
        let dupW = Array.copy hw
        for i in 0 .. 1 do dupW.[i] <- 0.0   // pair (bi=1, bo=0) weights (mult 2x1)
        let dupOut = Linear.homLinear hIn hOut dupW hx
        check "duplicate input irrep is reachable (unlike linear)"
            (Array.sub dupOut (hStarts.[0]) 6 |> Array.exists (fun t -> abs t > 1e-12))

        section "activations"

        checkClose "silu(0) = 0" 1e-15 0.0 (Activations.silu 0.0)
        checkClose "sigmoid(0) = 1/2" 1e-15 0.5 (Activations.sigmoid 0.0)
        checkClose "relu(-2) = 0" 0.0 0.0 (Activations.relu -2.0)
        checkClose "relu(3) = 3" 0.0 3.0 (Activations.relu 3.0)
        checkClose "silu(1) = sigmoid(1)" 1e-12 (Activations.sigmoid 1.0) (Activations.silu 1.0)

        let aSpec = mkSpec [ (0, Even, 4); (1, Odd, 3); (2, Even, 2) ]
        let af = randArray rng (totalDim aSpec)
        let rA = Rotations.randomRotation rng

        // Both activations are SO(3)-equivariant: gates/scales are invariants.
        checkArrayClose "gated activation equivariance" 1e-8
            (Rotations.applyRep aSpec rA (Activations.gated aSpec af))
            (Activations.gated aSpec (Rotations.applyRep aSpec rA af))
        checkArrayClose "norm activation equivariance" 1e-8
            (Rotations.applyRep aSpec rA (Activations.normAct aSpec af))
            (Activations.normAct aSpec (Rotations.applyRep aSpec rA af))

        section "invariant exits: scalars / norms"

        // scalars: shape + values (the l=0 entries verbatim), and rotation
        // INVARIANCE (not just equivariance) — the certified exit to plain
        // number land.
        let sOutInv = Activations.scalars aSpec af
        check "scalars: length = l=0 multiplicity" (sOutInv.Length = 4)
        check "scalars: copies block-0 entries verbatim"
            (sOutInv |> Array.mapi (fun i s -> s = af.[i]) |> Array.forall id)
        checkArrayClose "scalars rotation-invariant" 1e-8
            sOutInv (Activations.scalars aSpec (Rotations.applyRep aSpec rA af))

        // norms: per-(block, mu) 2-norms, rotation-invariant, and exact on a
        // hand slot (block 1 copy 0).
        let nOutInv = Activations.norms aSpec af
        check "norms: one slot per (block, mu)" (nOutInv.Length = 4 + 3 + 2)
        let starts0 = IrrepsIdx.blockStarts aSpec
        let hand =
            sqrt (af.[starts0.[1]] * af.[starts0.[1]]
                  + af.[starts0.[1] + 1] * af.[starts0.[1] + 1]
                  + af.[starts0.[1] + 2] * af.[starts0.[1] + 2])
        checkClose "norms: block-1 copy-0 = hand 2-norm" 1e-12 hand nOutInv.[4]
        checkArrayClose "norms rotation-invariant" 1e-8
            nOutInv (Activations.norms aSpec (Rotations.applyRep aSpec rA af))
        check "norms: scalar slots = |value|"
            ([ 0 .. 3 ] |> List.forall (fun i -> abs (nOutInv.[i] - abs af.[i]) < 1e-12))

        section "O(3): improper-element equivariance (the equiv(O3) claims)"

        // The parity content of the equiv(O3) discipline, validated ONCE
        // here: under inversion∘R each (l, p) block picks up paritySign(p).
        let rI = Rotations.randomRotation rng
        let wI = randArray rng (TensorProduct.weightDim eCfg)
        let xI = randArray rng (totalDim eCfg.Spec1)
        let yI = randArray rng (totalDim eCfg.Spec2)
        // Tensor product: p_out = p1·p2 makes the sign factors multiply
        // consistently through every CG path.
        checkArrayClose "TP improper-equivariance (parity rule)" 1e-7
            (Rotations.applyRepImproper eCfg.SpecOut rI (TensorProduct.tensorProduct eCfg wI xI yI))
            (TensorProduct.tensorProduct eCfg wI
                (Rotations.applyRepImproper eCfg.Spec1 rI xI)
                (Rotations.applyRepImproper eCfg.Spec2 rI yI))
        // homLinear: same-(l,p) pairs carry the sign straight through.
        checkArrayClose "homLinear improper-equivariance" 1e-8
            (Rotations.applyRepImproper hOut rI (Linear.homLinear hIn hOut hw hx))
            (Linear.homLinear hIn hOut hw (Rotations.applyRepImproper hIn rI hx))
        // gated with an EVEN scalar head is O(3)-equivariant...
        let gSpecEven = mkSpec [ (0, Even, 2); (1, Odd, 2) ]
        let gx = randArray rng (totalDim gSpecEven)
        checkArrayClose "gated improper-equivariance (even head)" 1e-8
            (Rotations.applyRepImproper gSpecEven rI (Activations.gated gSpecEven gx))
            (Activations.gated gSpecEven (Rotations.applyRepImproper gSpecEven rI gx))
        // ...and with an ODD (pseudoscalar) head it is NOT: the premise the
        // equiv(O3) judgment enforces is a real theorem, not pedantry.
        let gSpecOdd = mkSpec [ (0, Odd, 2); (1, Odd, 2) ]
        let gxo = randArray rng (totalDim gSpecOdd)
        let lhsOdd = Rotations.applyRepImproper gSpecOdd rI (Activations.gated gSpecOdd gxo)
        let rhsOdd = Activations.gated gSpecOdd (Rotations.applyRepImproper gSpecOdd rI gxo)
        check "gated with an odd head BREAKS improper equivariance"
            ((Array.map2 (fun a b -> abs (a - b)) lhsOdd rhsOdd |> Array.max) > 1e-6)
        // Invariant exits: norms always; even scalars invariant; odd
        // scalars flip by exactly the parity sign.
        checkArrayClose "norms improper-invariant" 1e-8
            (Activations.norms aSpec af)
            (Activations.norms aSpec (Rotations.applyRepImproper aSpec rI af))
        checkArrayClose "even scalars improper-invariant" 1e-12
            (Activations.scalars gSpecEven gx)
            (Activations.scalars gSpecEven (Rotations.applyRepImproper gSpecEven rI gx))
        checkArrayClose "odd scalars flip under improper" 1e-12
            (Activations.scalars gSpecOdd gxo |> Array.map (fun t -> -t))
            (Activations.scalars gSpecOdd (Rotations.applyRepImproper gSpecOdd rI gxo))
        // y_to: negated coordinates = improper action at R = identity on the
        // sh output — sh_spec's parities (-1)^l are exactly right.
        let vv = Rotations.randomUnitVector rng
        let idR = [| [| 1.0; 0.0; 0.0 |]; [| 0.0; 1.0; 0.0 |]; [| 0.0; 0.0; 1.0 |] |]
        checkArrayClose "y_to O(3): Y(-v) = improper-rep Y(v)" 1e-10
            (SphericalHarmonics.evalUpTo 2 (-vv.[0]) (-vv.[1]) (-vv.[2]) |> Array.concat)
            (Rotations.applyRepImproper (shSpec 2) idR
                (SphericalHarmonics.evalUpTo 2 vv.[0] vv.[1] vv.[2] |> Array.concat))

        // Scalar blocks: plain silu.
        let gOut = Activations.gated aSpec af
        let siluOk =
            [ 0 .. 3 ] |> List.forall (fun i -> abs (gOut.[i] - Activations.silu af.[i]) < 1e-15)
        check "gated: scalars get silu directly" siluOk

        // Gate wiring: L1o copy mu uses sigmoid(scalar mu % 4).
        let starts = IrrepsIdx.blockStarts aSpec
        let mutable gateOk = true
        for mu in 0 .. 2 do
            let g = Activations.sigmoid af.[mu % 4]
            for cc in 0 .. 2 do
                let i = starts.[1] + mu * 3 + cc
                if abs (gOut.[i] - g * af.[i]) > 1e-15 then gateOk <- false
        check "gated: higher-L copies scaled by sigmoid of their gate scalar" gateOk

        checkThrows "gated requires scalar first block" (fun () ->
            Activations.gated (mkSpec [ (1, Odd, 2) ]) (Array.zeroCreate 6) |> ignore)
