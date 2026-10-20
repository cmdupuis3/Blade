VERDICT: DO NOT MERGE
24. CLOSED — empty-slot arrows now count as callbacks; the nullary writer is joined and refused, while nonempty index-only arrows remain excluded.
25. NOT CLOSED — the direct second-order witnesses are fixed and the collector is shared, but an alias of a callback parameter still hides the indirect edge (27); tainting also introduces pure-program regressions (26, 28).

Reviewed git diff 463c3fee..804516d1; the three implementation files match worktree-bench-fortran at inspection. File/git reasoning only: NO builds, emits, tests, or probe execution. Builder gate totals and temporary traces are unverified. All probe outcomes below are source-derived.

26. BLOCKER — src/Optimize.fs:1146,1164-1167,1219; src/Lowering.fs:2837. A MODULE-level alias is misclassified as an untracked escape: roots contain only binding VALUES, so `let run = launch` is scanned as a bare `IRVar launch`, whereas the equivalent local `IRLet` is exempted. This permanently taints launch.f and newly refuses a pure program that the old collector resolves through Lowering's alias map. Concrete, nongeneric probe:

```blade
type I = Idx<2>
function ident(v: Float64) -> Float64 = v
function launch(f: (Float64) -> Float64) -> Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute
let run = launch
let r = run(ident)
```

Before: launch.f={ident}, r=[0.0, 1.0], team admitted. After: launch.f removed, BL4005. Nongeneric aliases survive IRMono.eliminateGenericAliases. Exact fix: preserve binding identity in the collector's input, register module aliases like local aliases, and classify their USES for escape; do not scan a tracked alias initializer as an escape. Pin this acceptance/value/team case.

27. HIGH — src/Optimize.fs:1158,1164,1178-1192. `resolve(k)` can chase an alias to a parameter h but returns None because h is not a concrete callable; targets then tests the ORIGINAL k against isCbParam. k is absent. The skipped alias initializer and skipped IRVar call head leave h's known candidates neither propagated nor tainted. Probe: in mutability-errors/036 retain every declaration/call and replace only via's body with:

```blade
{
    let k = h
    k(f)
}
```

launch.f stays {ident}; via.h={launch}, via.f={bump}, but k(f) is unknown and taintAll(bump) taints no callback parameters. The parallel kernel inside launch therefore admits the writer. The call through k is outside that kernel, so its own unknown-call summary does not rescue the refusal. Tuple-head variant of the SAME probe: `let k = h; let t = (k, 0); t[0](f)` (put each declaration on its own line). Scanning the tuple records k as an escape, but targets(k)=None again loses launch. Without the alias, `let t = (h, 0); t[0](f)` does taint launch.f and refuses, including the all-pure control. Exact fix: canonicalize aliases to callback-parameter ids BEFORE both candidate lookup and escape traversal; propagate actuals into every known target, retaining known targets even alongside unknown ones. Track tuple projections or conservatively taint their reachable callable owners. Pin both writing probes and pure controls.

28. BLOCKER — src/Optimize.fs:1191,1200,1208-1218. A not-yet-populated callback slot is treated as permanently unknown DURING discovery. Its arguments immediately taint their owners, and that taint is never withdrawn after the head resolves. This makes an all-pure third-order program newly fail depending on discovery order. Concrete probe:

```blade
type I = Idx<2>
type F = (Float64) -> Float64
type R = Array<Float64 like I>
type H = (F) -> R
type Q = (H, F) -> R
type D = (Q, F) -> R
function ident(v: Float64) -> Float64 = v
function launch(f: F) -> R = method_for(range<I>) <@> lambda(i) where omp(i: 1) -> f(Float64(i)) |> compute
function via(h: H, f: F) -> R = h(f)
function bridge(q: Q, f: F) -> R = q(launch, f)
function relay(h: D, f: F) -> R = h(via, f)
let a = launch(ident)
let b = relay(bridge, ident)
```

Roots seed launch.f={ident}, relay.h={bridge}, relay.f={ident}. Function bodies are scanned in declaration order: bridge.q is still empty when q(launch,f) is visited, so launch.f is tainted. Later in the SAME round relay.h(via,f) discovers bridge.q={via}; subsequent rounds discover the remaining pure edges, but launch.f stays removed. Before this commit the direct pure seed allowed launch's kernel; both results are [0.0, 1.0]. Exact fix: distinguish undiscovered/bottom from genuinely unknown/top; saturate known call edges before committing unknown-call escape taint, then propagate real escapes to stability. Preserve known targets independently of the unknown bit. Pin this acceptance/value/team probe and its reversed discovery order.

(a) After stripUnits, exactly IRTArrow with ZERO slots or at least one OUTER SVal is function-valued (identity ignored). Nonempty all-SIdx/SIdxVirt arrows, outer tuples/loops/computations/poly types are excluded; return types are not inspected. The same predicate serves callback parameters and unresolved function references. The intermediate index-only-array admission is not reintroduced.

(b) One collector: Lowering.refuseWritingJoinLegs at src/Lowering.fs:2837; C++ facts through CodeGenExprSupport.computeProgramCallbackArgs at :1708, installed by CodeGen.genModule at src/CodeGen.fs:2077 (genModuleSplit :2211); LLVM facts through that same wrapper at src/EmitLlvm.fs:8005. Lowering collects every module; C++ merges modules at CodeGen.fs:3868 and LLVM at EmitLlvm.fs:7992. Direct indirect-call forwarding and seen-set cycles are shared, but aliases/escapes are incomplete as above. Also, :1205 stops after 1000 rounds and :1221 returns the partial table without TOP; no unconditional convergence guarantee is established. Join-leg refusal and scheduleMayWrite/In still use Callbacks=None, conservatively.

(e) loops/506 pins r=[1.0, 2.0]; OmpTests.fs:632-635 requires BLADE_OMP_PARALLEL_FOR and forbids the serial-decline marker. 507/508 likewise pin pure values plus separate team cases. Refusals 035-038 pin BL4005, message, and spans 10:1/15:1/13:1/9:1. 039 pins BL4005 @16:1. Its summary round 0 writes a0; round 64 still adds a64 (65 storage positions), while captured G requires propagation through a67, so Optimize.fs:938's budget branch necessarily returns TOP. This is structural verification, not verification of the removed trace; the refusal pin alone would also pass if the budget were enlarged until natural convergence.

(c) No concrete regression identified in examples/ (including physics/), stdlib/, or bench/kernels by source inspection; their ordinary index-only array parameters remain excluded. Accepted pure-program regressions outside those checked-in trees are 26 and 28; this is not an executed acceptance census.
(d) YES: no-write programs can change from emitted C++ to BL4005 (26, 28); pure nullary callbacks regain emission (24), and the shared predicate also affects CSE/inlining/scheduling. No byte-identical-emission claim is justified.
