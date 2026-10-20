VERDICT: DO NOT MERGE
26. CLOSED — module binding ids are retained by both callers; alias initializers do not escape (loops/509).
27. CLOSED — evaluated local aliases and structural tuple projections propagate every known head target (mutability-errors/040–041; pure controls loops/510–511).
28. CLOSED — tracked, undiscovered names evaluate to bottom; the third-order edges saturate without discovery-order taint (loops/512–513).
budget. CLOSED — src/Optimize.fs:1291,1311–1313 stops after 10000 rounds and returns Map.empty if still changing: unknown callbacks/TOP, not a partial table.

Reviewed git diff 804516d1..362470f8 and source at those commits, not the root checkout's older HEAD. NO builds, emits, tests, or probe execution. Outcomes below are source-derived; builder gate totals/rescan claims are unverified. The diff contains all five team assertions (pragma required, serial-decline forbidden) and the G41 update.

29. REGRESSION / BLOCKER — src/Optimize.fs:1226,1238,1285,1310. Returning a LOCAL ALIAS of a callback now poisons the callback slots of its known target, even in an entirely write-free program. Exact probe:

```blade
type I = Idx<2>
type F = (Float64) -> Float64
type R = Array<Float64 like I>
type H = (F) -> R
function ident(v: Float64) -> Float64 = v
function launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute
function passthrough(h: H) -> H = {
    let k = h
    k
}
let a = launch(ident)
let b = passthrough(launch)(ident)
```

804516d1: launch.f={ident}; passthrough.h={launch}. Its escape walk sees k, but targets(k) cannot resolve a callable and k is not a callback parameter, so launch.f remains closed. Both calls are pure, with a=b=[0.0, 1.0]; launch retains its team. 362470f8: eval(k) becomes {launch}; the returned k is an escape, setting launch.f.Top=true. Export drops launch.f, and Lowering.fs:2859 refuses launch's parallel kernel with BL4005. All types are concrete: generic-alias elimination does not erase this IRLet (Lowering.fs:643; IRMono.fs:480). The nested call is outside any parallel body, so its unresolved result was not itself a previous refusal.

This is intentional conservatism in the new escape rule, but still an in-scope regression of an accepted program with NO writes anywhere. Returning a function does not introduce an unknown caller into this closed program. Preserve return flow (or otherwise prove the possible calls pure); simply hiding the alias again would reopen the writing case.

The same issue reaches tuple arguments through src/Optimize.fs:1196,1302: tuple-bearing parameters are not tracked, and passing a tuple recursively escapes its callable components even when the callee only reads its scalar component. Independent exact probe:

```blade
type I = Idx<2>
type F = (Float64) -> Float64
type R = Array<Float64 like I>
type H = (F) -> R
function ident(v: Float64) -> Float64 = v
function launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute
function tag(p: (H, Int64)) -> Int64 = p[1]
function via(h: H, f: F) -> R = {
    let k = h
    let t = (k, 0)
    let n = tag(t)
    h(f) + Float64(n)
}
let a = launch(ident)
let b = via(launch, ident)
```

Before: the tuple's k reference cannot taint launch; tag's non-callback argument is ignored; h(f) supplies ident, so a=b=[0.0, 1.0]. After: tag(t) escapes t's {launch} component and removes launch.f, causing the same BL4005. There are no writes.

Predicate coverage: module/local aliases and locally retained tuples are tracked; passing through a tuple parameter or returning an alias hits 29. Ordinary partial application eta-expands to a lifted lambda with a full-arity body call (TypeCheckInfer.fs:1527,1599); it does not normally hit the new short-IRApp widening. Lifted lambda parameters are included in funcs, so direct callback forwarding through them participates in the fixpoint. Bare named kernels still escape; naming a function with no callback parameters cannot taint any slot. Imported functions are collected across modules; arity/HM/shape specialization precedes Lowering's check (Lowering.fs:2946–2995), and both emitters use the shared collector. No separate concrete regression identified in examples/ (including physics/), stdlib/, or bench/kernels/ by source inspection; their index-only array parameters remain excluded. This is not an executed acceptance census.

Bottom/TOP: kept distinct throughout iteration; known targets remain alongside Top and are still propagated. Genuine escape taint interleaves with discovery monotonically; missing tracked values do not create it. Terminal missing/empty entries become unknown to consumers only after convergence or budget widening. That closes 28 and the budget defect, but does not repair the precision regression in 29.

Out of scope, for the gap register: none recorded.