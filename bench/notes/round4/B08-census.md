# B08 / PM07: C++ antisymmetric rank-two decompact initialization

2026-10-22. Source/design census only in b-measurement, HEAD `2a5c2e26`.
No production edit, build/lower/emission/test/native/benchmark, git mutation or
delegation. B07 sources and evidence remain frozen. Root reviews this census
before authorizing any emission or implementation. Graft reported no src index;
targeted source inspection supplied the anchors below.

## Disposition and one concrete target

The plan's PM07 crosswalk explicitly records existing initialization elision
(`peakmem-microkernel-plan.md:49,335`; sketch `01-lifetimes-folds-layout.md:226`).
Ordinary complete-write arithmetic maps are not a new blanket-zero-removal target:
C++ scalar pools return `new T[cells]` or uncleared arithmetic cache blocks
(`nested_array_utilities.hpp:24–58,151–163,243–279,535–545`), and the flat elementwise
nest assigns each required output cell (`CodeGenLoopNest.fs:2433–2452`). LLVM
`allocPool`/`materialize` explicitly allocate then perform complete fills
(`EmitLlvm.fs:2049–2074,5559–5610`); the shim has no payload memset
(`blade_llvm_shim.c:351–370`). These are source facts, not a native store count
inferred from the allocator's name. Required pointer-row/header construction is
separate from scalar payload initialization.

Recurrence elision is also already implemented: the checker proves a restricted
row/cell-read/no-while case, Lowering emits IRUninit for dense static shapes, and
C++/LLVM allocate without its zero former (`TypeCheckInfer.fs:14057–14104`,
`Lowering.fs:1305–1315`, `CodeGenExpr.fs:2097–2115`, `EmitLlvm.fs:5066–5076`).
Whole-prefix/guarded-history cases retain required zeros. Existing
`recursive-arrays/024_zero_fill_elision_boundaries.blade` pins both sides.

**Real remaining source candidate:** the C++ sole-group Float64
`AntisymIdx<2,N> -> decompact(...,0)` dense N×N result. Current owner is
`CodeGenExpr.materializeDecompactForm`, SymAntisymmetric/r=2 branch, lines
3016–3039. It emits: allocation, a full N×N zero pass, then a strict-pair scatter
that overwrites every off-diagonal cell. No allocator change is needed. Replace
only this branch's eligible F64 initialization pass by N diagonal stores in a
later approved card; retain the required output allocation and both scatter stores.
Root confirmed A05's ordinary-local lifetime work does not overlap this target.

## Reachable route and exact initialization ownership

`tests/corpus/index-types/035_decompact_antisymmetric.blade` is an existing reachable
source witness: computed antisymmetric Reynolds output, named anti, then named
`decompact(anti,0)` and a full dense result pin. CodeGenBinding's IRDecompact
dispatch reaches `genDecompactBinding` (`CodeGenBinding.fs:577,3887–3907`):
force the deferred operand first, infer element type, call the inline materializer,
register its exact MatPool, and append its statements. IRDecompact dispatch to the
builder is `CodeGenExpr.fs:2066–2067`. No new feature or artificial disabled policy
is needed to reach the owner. Actual current emissions are still a later gate.

The source order is precisely `forceCode`, then dense allocation, then **all**
zero stores, then the existing pair scatter. For a stored F64 operand the scatter
performs only in-bounds source reads, stores and unary sign; it does not read its
fresh destination. Source forcing/failures complete before destination creation.
The destination remains private until materialization finishes; any later binding
publication/consumer/print follows the complete generated statement sequence.
Allocation, row metadata, MatPool lifetime/escape and cleanup must stay unchanged.
No partially initialized result may escape on success or a recoverable failure.
Preserve deferred forcing, diagnostic/exception order and failure behavior; do not
move callbacks, source evaluation or observation into the construction sequence.

## Definedness/completeness proof and required control

Current scatter enumerates a in [0,N), b with b+1<N-a, and sets i=a, j=a+b+1.
For every i<j there is exactly one iteration (a=i,b=j-i-1). That iteration writes
out[i][j]=src[a][b] and out[j][i]=-src[a][b]. The two destinations are distinct,
no other iteration writes either, and together they cover exactly N(N-1) cells.
They never touch a diagonal. Adding out[i][i]=+0 for each i covers the remaining
N cells exactly once: every observable output cell is defined before any read.
Keep the existing scatter traversal and source expressions/sign operations.

The diagonal fill is **required initialization**, not dead work. N=1 is the
empty-scatter control: source has no strict pairs, scatter executes zero times,
but the one required dense cell must be positive zero. N>1 also needs every
diagonal explicitly zero; no cached/fresh payload content may supply the value.
Do not erase the diagonal or rely on an all-zero source/checksum to hide holes.
The proposed zero literal retains the old +0.0 diagonal; off-diagonal +0/-0
must retain the original copy and unary-negation behavior. A future implementation
needs a sign-sensitive zero witness rather than equality alone. No FP reassociation
or arithmetic replacement is part of this proposal.

Initial admission is one sole compact group, rank exactly two, Float64, bounded
positive static N, stored fully materialized operand and fully retained dense
output. Other types (especially class/complex construction), leading/trailing
groups, higher-rank antisymmetry, ragged/sparse layouts, dynamic/unknown shapes,
partial writes, early exits and accumulation remain on their existing routes.
N=0 is not added by this card. N=1 may exercise the existing zero-leaf source
sentinel; B03's outstanding sentinel/reclamation caveat remains active and is not
closed or counted as a capacity saving here. Preserve existing size/shape checks;
this census does not establish general C++ allocation-overflow coverage.

Source-level logical destination writes change from N²+N(N-1) to N+N(N-1)=N²,
removing N(N-1) redundant zero stores (8N(N-1) analytical payload-write bytes).
Required payload allocation remains 8N² bytes plus the same row metadata. These
are source-operation models, not measured native writes, allocation peak, RSS,
DRAM bandwidth or a speedup. A native compiler may already eliminate dead stores;
then the native no-gain result must be recorded rather than manufactured away.

LLVM is an existing distinct route/control, not a target for this C++ fill edit:
IRDecompact builds a logical-coordinate canonical-read producer at
`EmitLlvm.fs:5447–5488`, then materialization assigns each output cell, including
implicit diagonal zero. No symmetric/antisymmetric emitter equivalence or native
zero-write count is claimed from that description.

## Minimal later Luna census request, only after root approval

Author/freeze two small default-policy C++ emission inputs in a later approved
step, reusing 035's source idiom: A=[1.0,2.0,3.0,4.0,5.0], and A=[1.0] for N=1;
`g(x,y)=2*x+y` with its existing comm/Reynolds(Antisymmetric) construction, named
computed anti, then named dense result=decompact(anti,0). Require **both anti and
the full result** as outputs. For N=5 the independent dense contract is
result(i,j)=Float64(i-j), with +0 diagonal; N=1 result is [+0]. These are analytic
small-value expectations, not executed pins. If the empty strict source refuses,
record that exact gate; do not silently change shape or infer its initialization.

Sole Luna: use an exact source-matching compiler (one private Release build only
if matching reuse cannot be evidenced), then exactly these two raw C++ emissions,
no lower-only tool execution, native compile/run, validator, suite or benchmark.
Use absolute source/output/log paths and private cwd/TEMP/TMP. Save compiler,
source and raw emitted hashes plus complete command/stdout/stderr identities.
Check actual default topology: stored strict source, distinct complete dense
destination, allocation -> full zero pass -> original two-image scatter, full
output observation after construction; count each initialization/scatter bound.
The N1 emission must retain its one explicit diagonal store. Stop for root review.
Do not request LLVM or wider shape sweeps merely to fill a comparison table.

If those emissions confirm the source gap, the next implementation proposal is
only the eligibility-guarded diagonal-fill edit in CodeGenExpr.fs plus focused
complete-output/diagonal/signed-zero/empty-scatter regressions. Native store/DSE
and lifetime/endpoint evidence would require a separate root-reviewed runner
stage. No B03 instrumentation expansion or B07 adaptation/timing admission here.

## Source checkpoint identities

Raw working-tree SHA-256, inspected without executing project code:

| source | SHA-256 |
|---|---|
| src/CodeGenExpr.fs | 3E9B3D035BF268C969B578D1318965D9B98F5ADD48D8399071F6696233853BD5 |
| src/CodeGenBinding.fs | 829568D273B59E26CE1233C2848A0A387343537E49A1BAF6B803BEDF507AC597 |
| src/cpp/nested_array_utilities.hpp | FA12D552F27D4757C73B764E384A3F883510BFDBE15655DA352CEE4E32146DB9 |
| src/EmitLlvm.fs | 6A402EBF9F30687EA5ED6419A657C6B1AC41D234D0AC6A86EAAEEBBEBC3BA5C3 |
| src/cpp/blade_llvm_shim.c | 172754BE5A4E4A1976C8E0762304C90F87D79664CBE09695D71645FE05BA7888 |
| tests/corpus/index-types/035_decompact_antisymmetric.blade | 1C8951DD41425468BB034AB7395522BB46FEC49AB1515B8DED0332897885DA72 |

One compact source/design checkpoint only. Stop for root's source review.
