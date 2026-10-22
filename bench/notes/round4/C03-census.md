# C03 census: two private selected folds in one tuple host

Source/design only; no lowering, emission, execution, production edit or test run.
Integration inspected through `4ff7da4f8b38251f64341e628aaa736c37eea966`.
The C02-L1 runner checkout remains frozen. Graft returned no F# hits (one
indexed file); the source anchors below came from targeted direct inspection.

## Bounded proposal

Extend the existing `fuseSelectedTerminalModule` with ONE additional shape:
one top-level tuple host containing exactly two direct, explicitly seeded
Float64 folds of the same private selected array. Preserve C01's existing
single-terminal arm. Rewrite the two folds to guarded folds over the stored
dense input, then let the existing sibling-fold pass join their traversal.
Do not extend sibling regions across bindings or add a backend mechanism.

Representative source (explicit `BLADE_PRINT=result`, repro enabled):

```blade
let x = [-2.0, 0.0, 1.0, 3.0, -1.0, 2.0]
let keep = mask(x, lambda(v) -> v > 0.0)
let y = compound(x, keep)
let result = (reduce(y, (+), 7.0),
              reduce(y, lambda(a, b) -> a * 10.0 + b, -2.0))
```

Independent expected result: `(13, -1868)` from selected values `[1,3,2]`.
The second chain is `-2 -> -19 -> -187 -> -1868`; a false predicate must
return its accumulator, not perform a fold step with numeric zero.

**Source lowering is a hypothesis, not observed IR.** `Lowering.fs:673`
maps `TExprTuple` to `IRTuple`, but declaration lowering/destructuring may
introduce temporaries. Before implementation/runtime claims, the runner must
assert that the actual host has exactly this topology, using IDs and types:

```text
host.Value = IRTuple [IRReduce(IRVar y, k1, Some s1);
                     IRReduce(IRVar y, k2, Some s2)]
host.Type = IRTTuple [Float64; Float64]
```

Stop for review on another topology; do not widen matching by generated names,
prefixes, arbitrary let spines or cross-binding dependency discovery. A source
`let a=reduce(...); let b=reduce(...)` is deliberately outside this new arm.

## Existing route and the precise gap

* `Optimize.fs:3751-3925`: C01 accepts exactly one direct top-level seeded
  scalar `IRReduce`; terminal discovery at 3834 does not descend into a tuple.
  It synthesizes `if pred(cell) then fold(acc,cell) else acc` with fresh
  unlicensed callable/arrow metadata, removes mask/compound and records origins.
* `Optimize.fs:6639` (`sfWalk`) already visits tuple legs. `7238-7285` admits
  seeded `IRReduce` over a stored, plain dense literal-extent axis, proves its
  kernel total, and constructs an identity map leaf for the join.
* `Optimize.fs:7031` starts a sibling region; `7930` processes each binding
  separately. Merely rewriting two different top-level bindings would remove
  selection storage but leave two source scans. No cross-binding join is proposed.
* `Optimize.fs:7835-7864` creates the existing
  `IRReduceCompute(IRFusion leaves, IRTuple kernels, IRTuple seeds)` plus
  projections. Each leg retains its own seed, fold and result. Canonical leg
  keys at 7207 include float bits; distinct kernels/seeds must remain distinct.
* `CodeGenBinding.fs:6330` emits the join; its per-leg wrappers at 6491 and
  seed initialization at 6540 preserve independent fold state. LLVM's join
  plans at `EmitLlvm.fs:7474` and dense loop near 7544 do the same.
  `Interp/Loops.fs:2197-2267` evaluates each leg's own nest, preserving values
  without promising the native lane's traversal sharing.

Placement stays in the existing selected pass: after array-binop lowering and
whole-program `foreignRefs` collection (`Lowering.fs:3116-3123`), before
`optimizeModule` (3142), IRLift (3146), CSE, pool reuse and planner tables.
`shareSiblingFoldsModule` is already last in `optimizeModule` at
`Optimize.fs:7953-7970`. Thus no late side-table repair or new phase is needed.

## Admission and complete residual census

Keep C01's closed scalar proof (`Optimize.fs:3783-3816`): stored rank-1 dense
Float64, same exact input ID for mask and compound, capture-free total scalar
predicate and folds, closed pure/total Float64 seeds, no static/arity/OMP/CUDA/
MPI kernels. Do not broaden to transitive calls, selected maps, structured
elements, membership predicates, unseeded folds or external input rereads.
Additionally require the literal plain axis accepted by sibling `plainAxis`
(6860), so enabled sibling sharing has a supported native route.

For the new arm, require mask, compound and tuple host adjacent in executable
binding order (`ki + 1 = yi`, `yi + 1 = hi`). This intentionally refuses any
intervening abort, opaque call or unrelated evaluation rather than inventing a
new no-failure analysis. Continue the stored-source/alias proof and full
initializer/interval payload write checks through the host. Reject lifetime
events explicitly using the existing barrier analysis (`Optimize.fs:1605`).
`CseFacts.Repeatable` is insufficient: its contract explicitly permits MayFail
values (`Optimize.fs:787-829`); sibling's intervening checks mainly establish
availability/writes (`7429`), not arbitrary failure-order preservation.

Residual census must remove only the TWO matched fold operands in the actual
host binding ID. Traverse/audit both kernels, both seeds and all embedded types;
never exempt the entire host. Another binding holding a reference-equal
expression object still counts in full. Reuse the complete roots from
`compactionRoots` (3637), extent walkers (3570/3592) and side operands/payloads
(1480/1494), including:

* Every other value, side payload, alias, function body/capture and function type;
  declared index/type payloads, embedded extents and hidden execution metadata.
* Host type and every host side payload, whole-program callables, foreign IDs,
  provider source/name references. Ordinary host admission must reject execution
  side payloads even when they do not mention the private arrays.
* Explicit certification of the removed mask/compound's expected side operands
  and compound type; retain any additional type/dependency demand. A third fold,
  keyed read, alias escape, return/capture, other compound, print or hidden use
  causes the whole new arm to decline.

Printing is an actual observation: unset/empty `BLADE_PRINT` observes keep/y.
The pilot remains top-level because compound lowering is top-level-only
(BL6002). Require explicit valid print selection excluding keep/y; preserve
the original result binding ID/name/type. A later seed dependent on the first
result is outside the exact tuple shape and closed-seed proof, so remains
materialized. Unknown, failing or effectful predicate/fold/seed also declines.
Do not inherit reassociation/parallel permission through either new arrow.

## Claims to measure separately

1. **Storage:** remove private Bool mask and selected value/index storage only
   after the full census. Stored x (8N bytes) remains required. Selected values
   account for roughly 8m bytes plus allocation policy; index/positions and mask
   sizes depend on the existing compaction route. Inspect baseline: earlier
   compaction may already eliminate positions. No new positions saving is
   credited when the baseline lacks them. Two scalar accumulators remain.
2. **Traversal:** sibling ON should yield one dense native source traversal
   with two independent accumulators; sibling OFF may still elide storage but
   has two guarded source scans. Report the latter only as storage elision.
   Baseline mask/count/gather/fold traversal counts need emitted census.
3. **Predicate:** each guarded fold contains the predicate, so the joined
   logical loop can evaluate it twice per source cell. Named deferred-map
   sharing in `genReduceJoinCore` is a different mechanism. No predicate-sharing,
   single-load, speed or measured peak-memory claim without emitted/runtime
   evidence. A native compiler may subsequently CSE pure comparisons; do not
   use an effectful counter to infer behavior of this pure-only admission.

## Smallest validation request after root review

First ask Luna for lower/emit/plan of the representative tuple source against
an exact integration binary and the topology assertion above. This diagnostic
is not an optimization success assertion. If topology fits, proposed ownership
is the existing selected function in `src/Optimize.fs`, focused
`tests/OptimizeTests.fs` assertions and index-types corpus; no sibling/backend/
Lowering change is currently justified. Root must approve implementation first.

After implementation, pin values independently of optimizer output and assert
actual IR plus emitted C++ and real LLVM allocations/loop dataflow:

| Case | Required evidence |
| --- | --- |
| Two distinct folds/nonzero seeds above | `(13,-1868)`; no mask/selected stores; two accumulators |
| Predicate always false; zero source extent if accepted | Exact `(7,-2)`; no fold step; signed-zero empty seeds checked by bits |
| Cancellation `[1e16,-1e16,1]`, keep nonzero | Sum seed 1 and subtraction seed -2 give `(1,-3)` under serial contract-off |
| Sibling ON vs OFF | Same independent pins; one vs two native scans; storage elision distinguished from sharing |
| Compaction disabled/unshared, repro disabled | Existing materialized route retained; default licensed arithmetic unchanged |
| Default print or demanded keep/y; third/keyed/alias/capture/hidden-type demand | New arm declines; demands preserved |
| Failing predicate/fold/seed; intervening abort/write/lifetime event; dependent later seed | Decline and paired failure/seed ordering preserved |
| Unknown/parallel/structured kernel; separate terminal bindings | Narrow-shape refusal, no expansion of sibling regions |
| Existing C01 single terminal | Existing admission and emitted route unchanged |

Use per-call environment pins: repro=1, contract=off, BLAS=off, explicit result
printing; vary sibling and compaction gates independently. Run interpreter and
optimizer ON/OFF gates with the same print selection and independent pins:
both interpreter/backend lanes consume optimized IR. A generic opt-diff run
that clears BLADE_PRINT exercises refusal, not this positive route. Native
C++/LLVM ON/OFF checks establish backend capability and failure order; the
interpreter establishes semantics, not traversal count. No full-suite request.

Status: proposal only, awaiting root review and runner topology census.
