/// Linear-algebra dispatch classification — Phase 5 of
/// docs/plan-cpp-perf-exploitation.md.
///
/// PURPOSE. CodeGen must never decide for itself what a loop nest "is" in BLAS
/// terms. This module owns that judgement: it maps an IR node onto a typed
/// `LinAlgCall` descriptor naming a routine, the operand roles, the dimensions
/// and the transpose flags, and it owns the POLICY table that says whether a
/// recognised shape is actually routed through the `blade_linalg.hpp` shim or
/// deliberately left to Blade's own loop nest. CodeGen consults; it does not
/// pattern-match BLAS shapes.
///
/// WHY A SEPARATE MODULE. Blade's uniform representation wraps every operation
/// — even a scalar `T^0 -> T^0 -> T^0` binop like `*` — in at least a trivial
/// loop nest by codegen time (`lowerArrayBinOpsModule` rewrites raw array
/// binops into combinator form), so outside the two nodes v1 handles there is
/// no distinguished "matmul node" to hook. Recognising the rest means matching
/// LOOP-NEST SHAPES, jointly on (kernel op x fold op x operand ranks x index
/// ties): the same `*` is Hadamard at equal ranks, `scal` against a scalar,
/// half of `dot` under a `(+)`-reduce, and `gemm` under a contraction tie. That
/// enumeration is per-op and per-level and wants its own home, its own tests,
/// and its own growth path — not another arm inside a 14k-line codegen file.
///
/// V1 SCOPE (what is implemented here).
///   * `IRGram(l, r, sameArray)` -> Syrk (same array) / Gemm (distinct).
///   * `IRMatmul(a, b)`          -> Gemm.
/// Both are FIRST-CLASS IR NODES, so their classification is exact rather than
/// inferred; they need no nest matching and cannot be fooled by a coincidence
/// of shape. Everything below the `Planned` marker is skeleton + policy only.
///
/// GROWTH PATH (deliberately NOT implemented in v1 — see the plan).
/// The intended next step is a family of active patterns over the combinator
/// trees, consumed by one more `try*` in codegen's existing shortcircuit chain
/// (precedent: `tryGenFlatElementwiseNest` at the apply-combinator site):
///
///     let (|BlasL1|_|) (info: IRApplyCombinatorInfo) : LinAlgCall option = ...
///     let (|BlasL2|_|) (info: IRApplyCombinatorInfo) : LinAlgCall option = ...
///     let (|BlasL3|_|) (info: IRApplyCombinatorInfo) : LinAlgCall option = ...
///
/// each returning `Some descriptor` for a recognised nest shape and `None`
/// otherwise, so the codegen seam stays "BLAS match -> flat elementwise ->
/// nested emitter", each arm falling through on None. Each pattern lands one at
/// a time with a differential test against the native nest it replaces.
module Blade.LinAlgPatterns

open Blade.IR
open Blade.Types

// ============================================================================
// Descriptor
// ============================================================================

/// The BLAS level a routine sits at. Recorded on every policy row because the
/// routing decision is made per LEVEL, not per routine: L3 pays enormously
/// (blocking and microkernels are unreachable from emitted loops), L2 pays
/// modestly, L1-elementwise does not pay at all.
type BlasLevel =
    | L1
    | L2
    | L3

/// The routines this layer can name. `Dot`/`Gemv`/`Axpy`/`Scal` are declared —
/// not yet matched — so the policy table below can state their routing decision
/// explicitly rather than leaving it as an undocumented gap.
type LinAlgRoutine =
    /// C = A * B (general matrix product) — `blade_gemm`.
    | Gemm
    /// C = A * A^T, one triangle (symmetric rank-k update) — `blade_syrk`.
    | Syrk
    /// y = A * x (matrix-vector).
    | Gemv
    /// s = x . y (inner product).
    | Dot
    /// y = alpha * x + y.
    | Axpy
    /// x = alpha * x.
    | Scal

/// Where a recognised shape is actually EXECUTED.
type Routing =
    /// Through `blade_linalg.hpp` — which itself resolves to cblas or to the
    /// contract-preserving native fallback depending on the BUILD.
    | ViaShim
    /// Deliberately left to Blade's own emitted loop nest. A `Native` row is a
    /// RECORDED DECISION ("matched but routed native"), not a missing feature.
    | Native

/// The role an operand plays in a call, kept separate from the IR expression so
/// the emission site can decide how to obtain the pointer (pool base, staged
/// copy, ...) without re-deriving what the operand IS.
type OperandRole =
    /// Left/first factor.
    | RoleA
    /// Right/second factor.
    | RoleB
    /// Result.
    | RoleC

/// The concrete `blade_linalg.hpp` adapter a routed call lands on. A ROUTE is
/// narrower than a routine: `gram(A, A)` and `gram(A, B)` are both rank-k
/// updates in BLAS terms but reach different entry points because Blade's
/// symmetric result is PACKED triangular storage while its general result is a
/// dense pool — a difference the shim (not codegen) has to absorb.
type LinAlgRoute =
    /// `blade_gram_same` — A·Aᵀ into packed upper-triangular symmetric storage.
    | RouteGramSame
    /// `blade_gram_distinct` — A·Bᵀ into a dense pool.
    | RouteGramDistinct
    /// `blade_matmul` — A·B into a dense pool.
    | RouteMatmul

/// An operand as classified: the IR expression, its role, and whether the call
/// consumes it transposed.
type LinAlgOperand = {
    Role: OperandRole
    Expr: IRExpr
    Transposed: bool
}

/// How the extents of a call are named. v1 always resolves them at RUNTIME off
/// the operands' `.extents[]`, exactly as the pre-shim emission did — the
/// descriptor records WHICH extent of WHICH operand, so the emission site
/// spells the accessor and this module stays free of C++ text.
type DimSource = {
    /// Index into the call's operand list.
    Operand: OperandRole
    /// Which axis of that operand.
    Axis: int
}

/// A classified linear-algebra call.
type LinAlgCall = {
    Routine: LinAlgRoutine
    Route: LinAlgRoute
    Level: BlasLevel
    Routing: Routing
    Operands: LinAlgOperand list
    /// Rows of the result.
    M: DimSource
    /// Columns of the result (for Syrk this equals M).
    N: DimSource
    /// The contracted extent.
    K: DimSource
    /// Element type of the contraction. v1 shim routes require Float64.
    ElemType: IRType
    /// True when the result is written into Blade's packed triangular
    /// (symmetric) storage rather than a dense pool.
    PackedTriangularResult: bool
}

// ============================================================================
// Policy table
// ============================================================================

/// The routing policy, stated once and explicitly.
///
/// L1-ELEMENTWISE STAYS NATIVE. `axpy`/`scal` shapes are bandwidth-bound and
/// the flat elementwise loop (Phase 3) already vectorises them; a BLAS call
/// boundary buys nothing and costs a function call plus a staging decision.
/// L1 REDUCTION shapes (`dot`, `nrm2`) and all of L2/L3 are the paying routes,
/// with L3 >> L2 because blocking and microkernels are simply unreachable from
/// generated loop code.
///
/// A row exists for every routine this module can NAME, including the ones it
/// cannot yet MATCH, so "matched but routed native" and "not yet matched" are
/// distinguishable by reading one table.
let policy : (LinAlgRoutine * BlasLevel * Routing * string) list =
    [ Gemm, L3, ViaShim,
      "the one shape emitted loop code cannot approach; blocking/microkernels pay by orders of magnitude"
      Syrk, L3, ViaShim,
      "same as gemm, and it halves the work by computing one triangle — which is also Blade's storage"
      Gemv, L2, ViaShim,
      "pays modestly (bandwidth-bound but cache-blocked); NOT YET MATCHED — needs the reduce-over-nest patterns"
      Dot,  L1, ViaShim,
      "an L1 REDUCTION, unlike axpy/scal: the serial FP chain is the bottleneck and BLAS breaks it; NOT YET MATCHED"
      Axpy, L1, Native,
      "bandwidth-bound elementwise; the Phase 3 flat loop already vectorises it, so a call boundary is pure loss"
      Scal, L1, Native,
      "same as axpy — an elementwise scale is one vectorised pass either way" ]

/// The routing decision for a routine, from the table above.
let routingOf (r: LinAlgRoutine) : Routing =
    policy
    |> List.tryPick (fun (rr, _, routing, _) -> if rr = r then Some routing else None)
    |> Option.defaultValue Native

/// The BLAS level of a routine, from the table above.
let levelOf (r: LinAlgRoutine) : BlasLevel =
    policy
    |> List.tryPick (fun (rr, lvl, _, _) -> if rr = r then Some lvl else None)
    |> Option.defaultValue L1

/// The C++ entry point a routed call lands on. Kept here (not in CodeGen) so
/// the name of every shim function this compiler can emit is enumerable from
/// one place — the same reason `runtimeHeaderNames` is a single source of truth.
let shimEntryPoint (call: LinAlgCall) : string option =
    match call.Routing with
    | Native -> None
    | ViaShim ->
        match call.Route with
        | RouteGramSame -> Some "blade_linalg::blade_gram_same"
        | RouteGramDistinct -> Some "blade_linalg::blade_gram_distinct"
        | RouteMatmul -> Some "blade_linalg::blade_matmul"

// ============================================================================
// Classification entry points (v1)
// ============================================================================

/// v1 shim routes are real-Float64 only: `dsyrk`/`dgemm` and their native
/// fallbacks. Complex (`zherk`/`zgemm`) and float32 (`ssyrk`/`sgemm`) keep the
/// compiler's scalar loops — the SAME restriction the pre-shim BLAS lowering
/// carried, so this changes nothing about which programs use which arithmetic.
let private isRealDouble (t: IRType) =
    match t with
    | IRTScalar ETFloat64 -> true
    | _ -> false

let private elemOf (e: IRExpr) : IRType option =
    match typeOf e with
    | ArrayElem a -> Some a.ElemType
    | _ -> None

/// Classify `gram(l, r)`.
///
///   sameArray -> Syrk: square m x m written into PACKED upper-triangular
///                symmetric storage (Blade's own layout for the result type).
///   distinct  -> Gemm with B transposed: C(m x p) = A(m x n) * B(p x n)^T,
///                dense result.
///
/// Returns None (→ caller keeps its scalar loops) when either operand is not a
/// real-Float64 array.
let classifyGram (l: IRExpr) (r: IRExpr) (sameArray: bool) : LinAlgCall option =
    match elemOf l, elemOf r with
    | Some le, Some re when isRealDouble le && isRealDouble re ->
        if sameArray then
            Some { Routine = Syrk
                   Route = RouteGramSame
                   Level = L3
                   Routing = routingOf Syrk
                   Operands = [ { Role = RoleA; Expr = l; Transposed = false } ]
                   // C is m x m from A's leading axis; the contracted extent is
                   // A's trailing axis.
                   M = { Operand = RoleA; Axis = 0 }
                   N = { Operand = RoleA; Axis = 0 }
                   K = { Operand = RoleA; Axis = 1 }
                   ElemType = le
                   PackedTriangularResult = true }
        else
            Some { Routine = Gemm
                   Route = RouteGramDistinct
                   Level = L3
                   Routing = routingOf Gemm
                   Operands = [ { Role = RoleA; Expr = l; Transposed = false }
                                { Role = RoleB; Expr = r; Transposed = true } ]
                   M = { Operand = RoleA; Axis = 0 }
                   N = { Operand = RoleB; Axis = 0 }
                   K = { Operand = RoleA; Axis = 1 }
                   ElemType = le
                   PackedTriangularResult = false }
    | _ -> None

/// Classify `matmul(a, b)`: C(m x n) = A(m x k) * B(k x n), dense result, no
/// transposes. The first-class intrinsic's only classification.
let classifyMatmul (a: IRExpr) (b: IRExpr) : LinAlgCall option =
    match elemOf a, elemOf b with
    | Some ae, Some be when isRealDouble ae && isRealDouble be ->
        Some { Routine = Gemm
               Route = RouteMatmul
               Level = L3
               Routing = routingOf Gemm
               Operands = [ { Role = RoleA; Expr = a; Transposed = false }
                            { Role = RoleB; Expr = b; Transposed = false } ]
               M = { Operand = RoleA; Axis = 0 }
               N = { Operand = RoleB; Axis = 1 }
               K = { Operand = RoleA; Axis = 1 }
               ElemType = ae
               PackedTriangularResult = false }
    | _ -> None

/// The single entry point CodeGen calls: classify whatever node it is holding.
/// Returns None for everything this layer does not (yet) recognise, which is
/// the caller's signal to emit its ordinary loop nest.
let classify (e: IRExpr) : LinAlgCall option =
    match e with
    | IRGram (l, r, sameArray) -> classifyGram l r sameArray
    | IRMatmul (a, b) -> classifyMatmul a b
    | _ -> None

// ============================================================================
// Planned — combinator-tree matching (skeleton only; see GROWTH PATH above)
// ============================================================================
//
// let (|BlasL1|_|) (e: IRExpr) : LinAlgCall option = None
// let (|BlasL2|_|) (e: IRExpr) : LinAlgCall option = None
// let (|BlasL3|_|) (e: IRExpr) : LinAlgCall option = None
//
// These are left as comments rather than as `None`-returning stubs on purpose:
// a stub that always declines is indistinguishable at the call site from a
// pattern that has been implemented and simply did not match, which is exactly
// the confusion the policy table above exists to prevent. They arrive with
// their first real case.
