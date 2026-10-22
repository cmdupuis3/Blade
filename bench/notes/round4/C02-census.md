# C02 source census: PM10 rank-2 deferred partial fold

State: source census only, pending root review. No production/test-source edit,
build, emit, program execution, test or benchmark was performed for C02.
Checkout: `.round4-worktrees/c-terminals`, current C01 checkpoint `780a5915`
(root accepted/integrated C01 as `8c6571a7`). This census does not include the
concurrently reviewed A01/D01 branches.

Read: integration's `peakmem-microkernel-plan.md` section 5.4 and
`peakmem-sketches/01-lifetimes-folds-layout.md` PM10. Graft was queried first;
it reported nothing indexed under `src`, permitting targeted F# source reads.

## Decision proposed for review

There is a specific missing route: one anonymous scalar Float64 map over an
already-stored plain rank-2 matrix, immediately consumed by one explicitly
seeded innermost-axis fold. The current partial-fold desugar forces the mapped
matrix before handing out row views. Propose eliminating only that mapped
matrix, by folding its scalar kernel over each source row using existing
`IRReduceCompute`, storing the required rank-1 results.

This is distinct from the anonymous two-vector outer-product partial fold,
which ALREADY streams and must be a no-gain control, not another implementation.
Initial scope should be reproducible/serial Float64, static nonnegative extents,
stored source, explicit closed literal seed, total producer/fold and audited
transitive callees. Composed pipelines, rank 3, joins, named top-level producers,
unseeded folds and licensed/default graphs are separate follow-ons.

## Present paths and their static evidence

| Shape | Present route | Evidence |
|---|---|---|
| `reduce(method_for(a,b) <@> lambda(x,y)->..., op, seed)` with two named rank-1 operands | Already rewrites to an outer apply whose per-row body is a fused scalar fold over `b`; no rank-2 output pool | `TypeCheckInfer.fs:3377`–`3448`, `partialFold.outerProductRewrite`; emission pin `OptimizeTests.fs:483`, `outerProductPartialFoldStreams`; value controls `loops/206_deferred_outer_product_partial_fold_streams.blade` |
| `reduce(method_for(M) <@> scalar_kernel, op, seed)` for rank-2 `M` | Misses that two-operand syntactic arm; anonymous source is bound as `__pfsrc...`, then a row-mode map reads the full source | `TypeCheckInfer.fs:3452`–`3554`, `srcIsNamed` and `appliedOver`; final partial routing at `3699`–`3706` |
| Same one-matrix map with a block scalar kernel | Same rank/partial desugar; internal scalar normalization may remove small per-cell temporaries but does not remove the rank-2 row source | Same partial-fold code; generic scalar fusion's host gate below excludes the row consumer |
| Named top-level deferred or explicitly computed mapped producer, then partial fold | Named source bypasses synthetic `__pfsrc`, but row-map emission forces a deferred operand under its own name and it becomes printable; an explicit `compute` is already stored | `TypeCheckInfer.fs:3464`–`3479`, `3550`–`3554`; `CodeGenBinding.fs:1411`–`1427`, `2958`–`2974` |
| Plain rank-2 stored matrix partial fold | Reads existing rows; row-fold jam may process several rows with separate ascending accumulators. This is not map-output storage elision | `CodeGenLoopNest.fs:2972`–`3056`, `tryGenRowFoldJamNest`; `OptimizeTests.fs:3405`, `planRowFoldSrc` |
| Rank-2 composition followed by a partial fold | Composition preserves its inferred output type (`TypeCheckInfer.fs:9914`–`9940`), so a known rank-2 output reaches the generic partial source binding; it is not matched by the two-vector lambda rewrite. Final mapped matrix remains required by row-view emission, even if composing its stages avoids some intermediate stage pools | `TypeCheckInfer.fs:3197`–`3215`, `3452`–`3554`; `CodeGenBinding.fs:1453`–`1475`, `IRComposeApply` materialization. Request emission of the exact spelling before claiming reachability/capacity |
| Full rank-2 fold, `axes = 2` | Do not assume every deferred spelling already streams. Static dense unannotated full folds can take `rankKDesugar`, which binds an anonymous source and walks a scalar accumulator over it. The deferred `omp` case deliberately bypasses this materializing desugar and reaches the fused terminal | `TypeCheckInfer.fs:3717`–`3854`, especially `operandIsDeferred`/`deferredWithOmpKernel` and `srcIsNamed`; `Lowering.fs:975`–`993` only creates `IRReduceCompute` after the checker selects that typed form |
| Joined partial results | A reduction-join leg with `axes = n` is explicitly refused: partial folds return arrays, whereas join legs fold to scalars. Forcing partial results before joining their later scalar folds is a different pipeline | `TypeCheckInfer.fs:2946`; `IR.fs:197`–`215` describes scalar accumulator/join encoding |

The existing generic elementwise-fusion pass cannot close the missing case:
`IRMono.tilePlainInfo` (`IRMono.fs:2547`–`2564`) requires every
`KernelInputRanks` entry to be zero; the consumer partial fold's kernel takes
a whole rank-1 row. `tryFuse` applies that gate to the host at `2646`.

`Optimize.planHotRowFolds` (`Optimize.fs:2973`, `3038`, `3122`) is also different:
it schedules row-map values behind a stored producer and an outer scalar
combine, retaining the producer store. `fuseRowFoldsModule` (`6175`, `6290`)
targets a recursive buffer followed by a scalar row-fold terminal, not this
ordinary map-to-partial-array result. Its documented may-abort residual must
not become PM10's legality rule.

## Exact source witnesses for the runner

Primary candidate (no hidden-output flag is needed):

```blade
type R = Idx<2>
type C = Idx<3>
let M: Array<Float64 like R, C> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let rows = reduce(method_for(M) <@> lambda(v: Float64) -> v * v + 1.0, (+), 7.0)
// Independent arithmetic: rows = [24.0, 87.0]
```

The mapped rows are `[2,5,10]` and `[17,26,37]`. An order-sensitive variant
changes only the fold to `lambda(a,b) -> a * 2.0 + b` with seed `7.0`; the
independently derived outputs are `[84.0,213.0]`. Under strict settings these
pin each row's ascending chain and argument order, rather than merely its sum.

Existing-machine idiom the new lowering should reach:

```blade
let rows = method_for(M) <@> lambda(row: Array<Float64 like C>) ->
    reduce(method_for(row) <@> lambda(v: Float64) -> v * v + 1.0, (+), 7.0)
    |> compute
```

The outermost `|> compute` must apply to the row-map result in the parsed
source; use parentheses if needed to disambiguate the multiline spelling.
The equivalent IR is unambiguous: outer row apply over `M`, each row kernel
returns a seeded scalar `IRReduceCompute` over a scalar map of its row view.
`Lowering.forceCallableBody` (`Lowering.fs:490`) and normal IRLift must bind a
statement-shaped inner terminal before C++ rendering (`CodeGenExpr.fs:345`
has no expression-position renderer for `IRReduceCompute`).

Required/materialized controls:

```blade
let P = method_for(M) <@> lambda(v: Float64) -> v * v + 1.0 |> compute
let rows = reduce(P, (+), 7.0)
let sample = P(1, 2)
```

Default printing observes `P`, as does its keyed sample. Keep its store. Also
retain a named deferred control (`let P = ...` without `compute`): partial
fold forcing currently materializes it under its own name and adds it to
the auto-print list. Do not accidentally turn that control into a private
anonymous terminal by resolving through its binding. A selected print list
excluding `P` does not by itself authorize the first C02 arm to inline it.

## Bounded implementation to review (not yet authorized)

Specialize the existing semantic-equivalence pipeline in `Optimize.fs`, after
scalar map normalization and before IRLift/CSE and all scheduling side tables.
`optimizeModule` is at `Optimize.fs:7878`; C01 demonstrates the early Lowering
phase and derived-callable origin conventions. No new IR node or emitter-only
side table is needed. Do not add a second runtime partial-fold engine.

Match one private scoped producer map and its sole direct row-reduction
consumer, with equal plain rank-2 shape, scalar Float64 map/fold, explicit seed
and immediate use. Rebuild the row apply over the stored original matrix; its
new row kernel uses the original scalar producer body inside an existing
`IRReduceCompute`, with the exact original fold callable and seed. Remove only
the private mapped producer. Do not trust a generated-name prefix as proof:
establish the binding/use/type/capture/side-payload census by IDs and scope.
If the actual normalized tree differs, adjust this proposal only after Luna's
lowered/emission census, not by guessing a competing match path.

Reuse `IRTotality.auditApply`/`auditCallable` (`IRTotality.fs:297`–`325`) with
module+program callable resolution and the existing scheduling write analysis.
The whitelist already understands pure scalar blocks (`IRLet`, `:165`) and
audits resolved transitive calls; a repeatability summary alone is insufficient
to prove no failure. Initially require capture-free kernels/callees or decline
any capture whose version cannot be proved; reject writes, unknown calls,
dynamic source mutation, structured/ragged/compact/provider/virtual sources,
rank raising, unknown extents, extra observers and every unmatched use. Use
`storedArrayProof` through source aliases; examine every intervening actual
payload, including hidden side-table calls. Initial adjacency keeps this
motion proof small.

Require `Toolchain.fpReproEnabled()` for the first arm. An original materialized
row's licensed K-lane graph need not match the fused terminal's licensed graph;
this census does not claim their equivalence. Preserve the default licensed
route and all requested parallel strategies until separately reviewed.
Reuse the fusion gate/optimizer-off infrastructure, with explicit applied/
declined route evidence and an actual allocation assertion, not a fallback
labelled as streaming. Final gate naming/placement is for root review.

## Numerical, empty, provenance and failure contract

- Each row has its own accumulator and its own original explicit seed. Never
  add the seed once globally or merge row states. The original partial desugar
  places the seed expression in the row kernel; preserve its evaluation count
  and location. A literal first admission avoids moving calls/side effects.
- Rows are visited in their original outer order; cells in a row ascend on the
  innermost axis. Keep `fold(acc, map(cell))` and every original scalar rounding
  operation. Pin contraction-off when comparing exact results.
- `C=0,R>0` returns the exact seed for each row without calling the map/fold;
  `R=0` returns a correctly typed empty result and executes no row seeds.
  Verify existing empty-literal reachability before authoring pins. Unseeded
  forms stay on their original static/runtime empty-refusal paths; the current
  partial empty gate is `TypeCheckInfer.fs:3357`–`3374`.
- Preserve the original unreduced `R` index record/tag and result type, plus
  source `C` identity in the row view. Test a downstream consumer explicitly
  annotated with `Array<Float64 like R>`; rank alone is insufficient.
- Mandatory paired-failure refusal: a producer fails only on the last cell of
  row 1, while the row-0 fold would fail on its first cell. For example use a
  Float64 producer calling a helper with guarded integer division by zero at
  `v=6`, and a fold helper whose denominator is `Int64(floor(b-b))`. Original
  materialization reports the producer helper's BL8013/site first. Streaming
  row 0 would report the fold helper first; totality must decline it. Pin
  source-site/stack precedence as well as BL8013. A may-fail seed and a hidden
  writer are independent negative controls.

## Backend and storage evidence requested next

Luna alone should lower and emit the primary candidate, parenthesized row-wise
idiom, materialized/named controls, the already-streamed two-vector control,
and plain/block/composed/full/join variants. Save exact source and normalized
IR/callable shapes, C++ allocations/stores, plan decisions and explicit LLVM
success/refusal. No implementation should start before root accepts this census
and the proposed match shape is confirmed.

The allocation assertion must isolate the producer: stored input `M` remains
rank 2, so `Array<double,2>` is NOT globally forbidden. Current expected extra
storage is mapped `8*R*C` cells plus row-pointer metadata; target removes that
store/read pair while retaining `M` and the required `8*R` result. Removing a
full rank-2 pool is a source hypothesis until emitted evidence confirms it.
No allocation curve, measured memory reduction or runtime gain is claimed.

If approved and implemented as an IR rewrite, require interpreter ON/OFF with
independent small row pins as well as C++ ON/OFF and interpreter/backend parity.
Both evaluators otherwise share the rewritten tree and can agree on the same
optimizer bug. Existing interpreter paths are `Interp/Loops.fs:2352` and
`:2367`; existing LLVM `IRReduceCompute` rendering is `EmitLlvm.fs:2937`.
Unsupported LLVM/provider cells must stay explicit refusals/skips, not silent
fallback passes. Check the row-wise idiom's LLVM emission before promising it.

After correctness and route evidence, the plan's scaling gate fixes C while
growing R, then fixes R while growing C, using odd sizes and quiet serialized
measurements. This next request is an emission/IR census, not a benchmark or
full-suite launch. No production edit is proposed for the already-covered
two-vector outer-product arm.
