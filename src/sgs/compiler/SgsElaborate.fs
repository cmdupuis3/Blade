/// sgs-module elaboration: the subgrid-closure field formers as compile-time source synthesis, mirroring the math elaborator.
///
/// Surface (reachable only through `import sgs [as <alias>]`; W is a `let static` name or literal). The ops read a
/// STATIC shape (this pass runs before type inference and the generated former is specialized to the extents), which
/// Blade.ElabShapes answers for every domain elaborator: a declared annotation, an array literal / cast, the result of an
/// elaborated call (`sgs.box_filter(sgs.grad(U, DX), W)`-style nesting, a math call surviving to this pass in surface
/// form), or an alias, destructured component or projection of any of those. A user-declared field element is judged as before #62; an inferred one must be Float64.
///
///   sgs.grad(U, DX)        -- (3, n, n, n) -> (3, 3, n, n, n): G(c,d,i,j,k)
///                             = d_d u_c, 2nd-order central diff, periodic
///   sgs.box_filter(U, W)   -- (3, n, n, n) -> (3, m, m, m), m = n/W: tile means
///   sgs.stress(U, W)       -- (3, n, n, n) -> (6, m, m, m): exact subgrid
///                             stress tau_ij = mean(u_i u_j|tile) - mean_i mean_j,
///                             packed in CartesianBridge.packPairs order
///
/// For each distinct (op, resolved shape/config) the elaborator synthesizes ONE Blade function (`__sgs_1`, ...) via
/// Blade.Sgs.Decls; call sites rewrite to the generated names, deduped by fingerprint.
///
/// Pipeline position: AFTER ML elaboration (so `where ml.galilean` bodies can be judged with surface `sgs.*` calls still
/// visible at ML's seam) and before PPL/Math/Grad -- generated bodies remain plain differentiable Blade source. The
/// surface shapes are registered (`registerShapes`) before ANY elaboration runs.
module Blade.Sgs.Elaborate

open Blade.Ast
open Blade.StaticEval
open Blade.ElabShapes
open Blade.Sgs.Decls

/// The steer appended to every "no static shape here" rejection.
let private steer =
    "sgs ops specialize the generated former to the extents at compile time, so annotate the binding (`let u: Array<Float64 like Idx<3>, Idx<n>, Idx<n>, Idx<n>> = ...`) or ascribe the argument"

let private staticInt (statics: StaticEnv) (what: string) (e: Expr) : Result<int, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (int n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some (SVInt n) -> Ok (int n)
        | Some _ -> Error $"{what}: expected a static int"
        | None -> Error $"{what}: '{name}' is not a `let static` binding (sgs op configs must be static)"
    | _ -> Error $"{what}: config argument must be a `let static` binding name or literal"

// Op elaboration

// Galilean stamping: the ML twin of this pass (MLElaborate's `equivStamp`) pins `__ml_equiv` on the kernels it
// synthesizes; the sgs formers carry the SAME kind of by-construction theorem via MLGalilean's discipline -- a constant
// boost u -> u + U0 is annihilated by every one of the two bodies stamped below, so the elaborator pins what it knows
// rather than leaving a loop nest for a later pass to re-derive.
//
// The conjunct is the NORMALIZED `__ml_galilean`, whose args NAME the boost-variant parameters (every other parameter is
// boost-invariant, and the certified result must be boost-invariant -- MLGalilean's rule); it is registered by
// `Blade.ML.Elaborate.expandStr`, which runs unconditionally first, so the name resolves even in a sgs-only program.
//
// ORDERING makes this safe: the galilean judgment runs INSIDE MLElaborate.expandModule over that pass's `decls1`, and sgs
// elaborates strictly AFTER ML, so the decls stamped here cannot exist when `buildCertTable` / `judgeFunction` /
// `inferGalileanCertificates` run -- keeping their bodies (flat work arrays, index arithmetic) out of a judgment they were
// never meant to face.
//
// box_filter is NOT stamped: its seam rule is STATUS-PRESERVING rather than invariant-producing (`sgs.box_filter(U, W)`
// maps a boost-variant field to a boost-variant field, since the weights sum to 1: filter(u + U0) = filter(u) + U0). A
// `__ml_galilean` certificate asserts a boost-INVARIANT result, so stamping it would be a false axiom, not a weaker one.
let private galileanStamp (boostParams: string list) (fd: FunctionDecl) : FunctionDecl =
    let conj = ("__ml_galilean", boostParams)
    let wc =
        match fd.WhereClause with
        | Some w -> { w with Custom = w.Custom @ [ conj ] }
        | None ->
            { Commutativity = []; Antisymmetry = []; Parallel = []
              Repro = false; TDims = []; Custom = [ conj ] }
    { fd with WhereClause = Some wc }

let private opList = "grad, box_filter, stress"

/// The (3, n, n, n) cubic-field shape every sgs former reads.
let private fieldShape (env: Env) (scope: Scope) (what: string) (uE: Expr) : Result<int, string> =
    arrayShape env scope what steer uE |> Result.bind (fun (label, s) ->
    staticExtents what label s |> Result.bind (fun dims ->
    requireFloat64 env what label s |> Result.bind (fun () ->
        match dims with
        | [ 3; a; b; c ] when a = b && b = c -> Ok a
        | [ 3; _; _; _ ] -> Error $"{what}: the field must be cubic (Idx<3> then three equal spatial extents)"
        | _ -> Error $"{what}: the field must be Array<Float64 like Idx<3>, Idx<n>, Idx<n>, Idx<n>> (component-first, space-last)")))

let private tileArg (env: Env) (what: string) (n: int) (wE: Expr) : Result<int, string> =
    staticInt env.Statics (what + " W") wE |> Result.bind (fun w ->
        if w < 1 then Error $"{what}: W must be >= 1"
        elif n % w <> 0 then Error $"{what}: the tile width W = {w} must divide the spatial extent n = {n}"
        else Ok w)

let private elabOp (st: GenState) (env: Env) (scope: Scope) (op: string) (args: Expr list) : Result<Expr, string> =
    // Every generated routine takes Float64 arrays: an inline float-literal argument takes that width.
    let args = args |> List.map ascribeFloatLiteral
    match op, args with
    | "grad", [ uE; dxE ] ->
        fieldShape env scope "grad" uE |> Result.map (fun n ->
            // Boost-variant in `u`: every emitted cell is (u(..+1..) - u(..-1..)) / (2 dx), a difference of two reads of
            // the SAME field, so the central-difference weights sum to zero and constant U0 cancels identically at every
            // grid point (`dx` is held-fixed). Exactly MLGalilean's axiom `sgs.grad(U, DX) : U any -> BInv`.
            let nm = ensureT st (fingerprint "grad" (box n)) "grad" (fun nm -> galileanStamp [ "u" ] (gradDecl nm n))
            syn (ExprApp (syn (ExprVar nm), [ uE; dxE ])))
    | "grad", _ -> Error "grad: expected grad(U, DX) with DX the grid spacing"
    | "box_filter", [ uE; wE ] ->
        fieldShape env scope "box_filter" uE |> Result.bind (fun n ->
        tileArg env "box_filter" n wE |> Result.map (fun w ->
            let nm = ensureT st (fingerprint "box_filter" (box (n, w))) "box_filter" (fun nm -> boxFilterDecl nm n w)
            syn (ExprApp (syn (ExprVar nm), [ uE ]))))
    | "box_filter", _ -> Error "box_filter: expected box_filter(U, W)"
    | "stress", [ uE; wE ] ->
        fieldShape env scope "stress" uE |> Result.bind (fun n ->
        tileArg env "stress" n wE |> Result.map (fun w ->
            // Boost-variant in `u`: each packed slot is the SECOND CENTRAL comoment over the filter cell,
            // prodsum(u_a, u_b)/T - mu_a*mu_b. Under u -> u + U0 the raw moment picks up
            // U0_a*mu_b + U0_b*mu_a + U0_a*U0_b and the product of means picks up precisely the same, so the difference is
            // unchanged: a central moment of any order >= 2 is boost-invariant, matching MLGalilean's axiom.
            let nm = ensureT st (fingerprint "stress" (box (n, w))) "stress" (fun nm -> galileanStamp [ "u" ] (stressDecl nm n w))
            syn (ExprApp (syn (ExprVar nm), [ uE ]))))
    | "stress", _ -> Error "stress: expected stress(U, W)"
    | _ -> Error $"sgs: unknown op '{op}' (available: {opList})"

/// Register the sgs surface ops with the shared shape oracle. Idempotent; TypeCheck runs it before the FIRST pass.
let registerShapes () =
    registerDomain "sgs" (scratchShape "__sgs_scratch_" elabOp)

/// Boundary: string-errored internals -> coded diagnostics (BL5600).
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    registerShapes ()
    expandDomain "sgs" "__sgs_" "BL5600" elabOp program