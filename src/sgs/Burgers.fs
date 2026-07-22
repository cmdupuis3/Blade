namespace BladeSgs

/// Burgers DNS -> LES oracle (corpus sgs/015 + examples/08): viscous Burgers
/// on a periodic grid, box-filtered to a coarse grid, exact 1-D subgrid
/// stress tau = mean(u^2|tile) - ubar^2, a Smagorinsky-form closure with the
/// coefficient learned by least squares from the DNS (a priori), and the
/// a posteriori LES run — with and without the closure — against the
/// filtered DNS truth.
///
/// Every expression MIRRORS the Blade source term for term (advective form,
/// central differences, the exact update order), and the initial condition
/// is printed as a literal, so the corpus pins are bit-exact end to end.
///
/// Config (FROZEN): nD = 64, W = 4 -> nL = 16, L = 2pi, hD = 2pi/64,
/// hL = 2pi/16, nu = 0.05, dt = 0.01, 80 DNS steps; a priori samples every
/// 10 steps (t = 0, 10, ..., 70); Delta = hL.
/// Closure: tau_model = -2 C Delta^2 |S| S,  S = central diff of ubar.
module Burgers =

    let private f2s (v: float) : string =
        let s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

    let private arr (name: string) (xs: float[]) =
        printfn "%s = [%s]" name (xs |> Array.map f2s |> String.concat ", ")

    let nD = 64
    let w = 4
    let nL = 16
    let hD = 2.0 * System.Math.PI / 64.0
    let hL = 2.0 * System.Math.PI / 16.0
    let nu = 0.05
    let dt = 0.01
    let steps = 80

    let u0 () : float[] =
        Array.init nD (fun i ->
            let x = float i * hD
            sin x + 0.5 * cos (2.0 * x + 0.6))

    /// One advective-form step, mirroring the Blade loop exactly.
    let step (n: int) (h: float) (visc: float[] -> int -> float) (u: float[]) : float[] =
        // visc supplies the extra flux-divergence term (the LES closure);
        // the DNS passes (fun _ _ -> 0.0).
        Array.init n (fun i ->
            let ip = (i + 1) % n
            let im = (i + n - 1) % n
            u.[i] + dt * (nu * ((u.[ip] - 2.0 * u.[i] + u.[im]) / (h * h))
                          - u.[i] * ((u.[ip] - u.[im]) / (2.0 * h))
                          - visc u i))

    let boxFilter (u: float[]) : float[] =
        Array.init nL (fun j ->
            let mutable s = 0.0
            for d in 0 .. w - 1 do
                s <- s + u.[j * w + d]
            s / float w)

    /// Exact 1-D subgrid stress per coarse cell.
    let exactTau (u: float[]) (ub: float[]) : float[] =
        Array.init nL (fun j ->
            let mutable ps = 0.0
            for d in 0 .. w - 1 do
                ps <- ps + u.[j * w + d] * u.[j * w + d]
            ps / float w - ub.[j] * ub.[j])

    let sbar (ub: float[]) (j: int) : float =
        let jp = (j + 1) % nL
        let jm = (j + nL - 1) % nL
        (ub.[jp] - ub.[jm]) / (2.0 * hL)

    /// tau_model = -2 C Delta^2 |S| S (Smagorinsky form; C learned).
    let gShape (s: float) : float = -2.0 * hL * hL * abs s * s

    let dump () =
        printfn "// ===== Burgers DNS->LES oracle (sgs/015, examples/08) ====="
        printfn "// nD=%d W=%d nL=%d nu=%s dt=%s steps=%d hD=2pi/64 hL=2pi/16" nD w nL (f2s nu) (f2s dt) steps
        let u0v = u0 ()
        arr "u0" u0v
        arr "ubar0" (boxFilter u0v)

        // ---- DNS trajectory, a priori samples every 10 steps ----
        let mutable u = Array.copy u0v
        let mutable sumTG = 0.0
        let mutable sumGG = 0.0
        for t in 0 .. steps - 1 do
            if t % 10 = 0 then
                let ub = boxFilter u
                let tau = exactTau u ub
                for j in 0 .. nL - 1 do
                    let g = gShape (sbar ub j)
                    sumTG <- sumTG + tau.[j] * g
                    sumGG <- sumGG + g * g
            u <- step nD hD (fun _ _ -> 0.0) u
        let c = sumTG / sumGG
        printfn "c_learned = %s" (f2s c)
        let dnsFinalBar = boxFilter u
        arr "dns_final_bar" dnsFinalBar

        // ---- a posteriori LES: coarse stepper + d(tau_model)/dx ----
        let lesRun (cc: float) : float[] =
            let closure (ub: float[]) (j: int) : float =
                // d(tau_model)/dx by central difference of the cell closure
                let jp = (j + 1) % nL
                let jm = (j + nL - 1) % nL
                let tp = cc * gShape (sbar ub jp)
                let tm = cc * gShape (sbar ub jm)
                (tp - tm) / (2.0 * hL)
            let mutable ub = boxFilter u0v
            for _t in 0 .. steps - 1 do
                ub <- step nL hL closure ub
            ub
        let lesModel = lesRun c
        let lesNoModel = lesRun 0.0
        arr "les_final" lesModel
        arr "les_final_nomodel" lesNoModel
        let err (a: float[]) =
            let mutable s = 0.0
            for j in 0 .. nL - 1 do
                s <- s + (a.[j] - dnsFinalBar.[j]) * (a.[j] - dnsFinalBar.[j])
            s
        let eM = err lesModel
        let eN = err lesNoModel
        printfn "err_model = %s" (f2s eM)
        printfn "err_nomodel = %s" (f2s eN)
        printfn "// closure improves the a posteriori run: %b (%.6g < %.6g)" (eM < eN) eM eN
