VERDICT: DO NOT MERGE
22. NOT CLOSED — every enumerated candidate is joined correctly, but the candidate collectors omit indirect higher-order call sites; a later writer can still be absent (25).
23. CLOSED — src/Optimize.fs:921-945 widens to External=true plus every storage parameter and deletes summaries created under the unfinished approximation; the old ten-parameter witness is addressed. This is nested per-callable iteration, not SCC-wide iteration.

Reviewed git diff d9289387..463c3fee. The four reviewed implementation/test files also match integration HEAD ba585a28. Source/git reasoning only; no builds, emits, tests, or witness execution. Reported gate totals are unverified.

24. BLOCKER — src/Optimize.fs:732,738; src/Lowering.fs:2843; src/CodeGenExprSupport.fs:1717. The new callback filter excludes NULLARY functions: IRTArrow([], ret, None) has no SVal. That representation is explicitly reserved for nullary functions (src/Types.fs:1300; src/IR.fs:926-942). Previously the IRTArrow filter recorded their arguments and resolved them. Now f() reaches callAt's unknown-callee branch and a previously accepted PURE parallel kernel is refused BL4005. Source-derived regression:

```blade
function one() -> Float64 = 1.0
function launch(f: () -> Float64) = method_for(0..2) <@> lambda(i) where omp(i: 1) -> f() + Float64(i) |> compute
let r = launch(one)
```

Exact fix: include empty-slot arrows in the shared function predicate, while continuing to exclude nonempty index-only arrows: slots.IsEmpty || List.exists (function SVal _ -> true | _ -> false) slots. Add this acceptance/value/team regression and a nullary writing-callback refusal; apply the corrected classification to unresolved callable references too.

25. HIGH — src/Lowering.fs:2838-2849; src/CodeGenExprSupport.fs:1712-1720. Both collectors resolve only statically named IRApp heads. They never propagate the actual arguments of h(f) to the parameters of h's candidate callables. One direct pure call gives launch.f a nonempty, apparently complete set; a second-order invocation supplies bump without adding it. The parallel kernel inside launch is therefore judged pure. The new joins cannot join a missing candidate. Source-derived witness:

```blade
type I = Idx<2>
let mut G = [0.0]
function pure(v: Float64) -> Float64 = v
function bump(v: Float64) -> Float64 = {
    G(0) = G(0) + 1.0
    G(0)
}
function launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute
function via(h: ((Float64) -> Float64) -> Array<Float64 like I>, f: (Float64) -> Float64) -> Array<Float64 like I> = h(f)
let a = launch(pure)
let b = via(launch, bump)
```

The recorded sets are launch.f={pure}, via.h={launch}, via.f={bump}; the missing edge is via's h(f) -> launch.f. Passing bump occurs outside the parallel body, so the front-end lexical/transitive check does not rescue it. Exact fix: compute the callback argument graph to a fixed point, resolving each indirect call head to ALL possible callable targets and propagating its actual arguments into each target's parameter slots. Unknown/escaping targets must taint affected parameters rather than leave a partial set certified complete. Share the collector between Lowering and both emitters. Pin this refusal and its reversed-order, all-pure, and lambda-argument controls.

(a) Lowering's parallelFacts and both emitters use the joined callAt/kernelRef implementation (Optimize.fs:1028-1070); candidate parameter writes are mapped to the current call's actuals, and unresolved candidates contribute External under UnknownIsWrite=true. They do NOT all use identical facts: join-leg refusal (Lowering.fs:2765) and scheduleMayWrite/In (Optimize.fs:1180) install Callbacks=None and conservatively decline unresolved callbacks. Lowering also chases aliases to callback parameters; backend callback lookup is by the raw id. No recorded sites means None, hence unknown/top at a call or ordinary function reference, not an empty/bottom candidate list. Imports are scanned across modules; an exported/unused function with no known application is likewise unknown. A forwarding-only callback cycle can have a nonempty table with no concrete seed and contributes bottom on revisiting its id. Literal lambdas are lifted to callable IRVars (Lowering.fs:1144-1218), so ordinary direct lambda arguments resolve; second-order propagation is missing as in 25.

(b) The budget is per callable, rounds 0..64, not per SCC, and widening is not broadcast to every member. Concrete IR SCC: 67 storage parameters; A writes p0 in its base arm and otherwise calls B with unchanged parameters; B calls C unchanged; C calls A(p1,...,p66,G). Starting at A, each B/C computation stabilizes against A's cached approximation in at most two evaluations; A alone accumulates one parameter per round and hits the cutoff. dropNew (Optimize.fs:930-936) deletes B/C, then lines 943-945 install A=top. A subsequent demand for B/C misses the cache and reads widened A; no stale B/C summary survives. The External bit cannot be erased by fresh actual arguments, so eager top for every SCC member is unnecessary for safety here. This is an IR example: direct runtime mutual declarations are not legal Blade. Test 032 exercises ten-step convergence, not the new 64-round widening branch; a >65-parameter/cyclic analysis regression is still absent.

(c) isFunctionValuedType strips only IRTUnitAnnotated wrappers and accepts exactly IRTArrow whose OUTER slots contain at least one SVal (including mixed index/value slots). It rejects empty arrows (24), index-only SIdx/SIdxVirt arrows and ordinary Array<... like I> partial views, IRTLoop, IRTTuple, IRTComputation, IRTPoly, and all other outer constructors; it does not inspect an arrow's result or a tuple's contents. A kernel reference is an expression, not a distinct IR type: a known IRVar callable resolves independently of this predicate. Rejected types can still contain writing code: nullary closures directly, tuples/arrays of functions, bound loop kernels, and deferred array producers. Rejection by this predicate is therefore not a purity proof. Explicit unresolved applications fail closed; known/deferred expressions are walked separately. The enclosing-mut-parameter refusal in 034 comes from unchanged TypeCheckSupport.fs:8006-8016; direct own-body writes remain legal.

(f) YES: OmpTests.fs:623-631 requires BLADE_OMP_PARALLEL_FOR and forbids "requested but emitted serial" in both new cases; the runner checks both lists at 1303-1315. This is a substring team-macro assertion (also matches PARALLEL_FOR_SIMD), not an exact single-pragma assertion. 030/031 pin BL4005, message and declaration span; 505 pins r=[0.0,1.0].

(d) No newly refused existing program identified in examples/ (including physics/), stdlib/, or bench/ from source inspection: their affected parallel paths do not pass nullary callbacks, and index-only array parameters no longer enter the callback table. This is not an executed acceptance census; 24 is a concrete accepted-program regression outside those checked-in examples.
(e) YES, pure emission can change: a recursive callback-forwarding cycle with a concrete pure seed now resolves instead of poisoning the whole candidate set (505 shape), enabling previously declined backend ivdep/SIMD/fold forms, or newly admitting a requested team at lowering. No such callback-cycle shape identified in bench/kernels or examples/; mandelbrot's ordinary pure recursion was already repaired before this diff. No byte-identity claim is established by this read-only review.

