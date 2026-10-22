# C06 source census: PM09 higher-rank full-fold composition

Source-only proposal on lane HEAD `37a7f24f1e9d2fd0561a6e1d81f0dedcbe5ecc30`,
after accepted C05 integration78c0e903. No production edit, build, lowering,
emission, interpreter/native run, test, oracle or benchmark by the coder.
All topology/allocation statements below distinguish static evidence from
unexecuted hypotheses. Existing `.r4tmp` and runner artifacts are untouched.

## Finding and one proposed target

There is a specific plain rank2 full-fold materialization route to census.
`TypeCheckInfer.fs:3717-3850` rankKDesugar runs BEFORE tryInferReduceCompute
(3889 onward). For static plain scalar axes, absent an OMP-requesting kernel,
it binds an anonymous operand, initializes ONE scalar accumulator, then emits
an ascending row-major loop nest updating that accumulator. It does not write
row partials or reset the seed per row. A named operand is read in place.
The OMP path deliberately skips the desugar and can use a fused terminal;
adding a strategy annotation is not the requested plain serial route.

Existing targeted regression `OptimizeTests.fs:1555-1615` independently names
`rank2_full_fold_materialized_desugar`: serial IRForRange, a mapped binding
indexed by that nest, no IRReduceCompute, LLVM input6x8 + mapped6x8 in both
FUSION arms. This is already validated historical C02-L1 evidence for that
specific2x3 source, NOT new C06 emission or a claim about every full fold.

The existing rank1 route is the positive control: TypeCheckInfer's fused
terminal dispatch and Lowering.fs974-995 turn a deferred apply into
IRReduceCompute. CodeGenBinding.fs5052 onward emits scalar accumulator(s),
EmitLlvm.fs7495 onward walks the virtual array, and Interp/Loops.fs2157 onward
mirrors that terminal. These routes already support composed scalar kernels;
no new backend operation is proposed.

ONE candidate, only after topology/emission review: recognize the exact private
rank2 scalar-map + single-accumulator serial full-fold nest and replace that
whole private region with existing IRReduceCompute(original map, original fold,
original literal seed). Initial scope: stored dominating dense unitless Float64
M, plain static R/C, one closed total scalar map and fold, seeded repro-only,
ordinary scoped host. Reuse the existing early terminal optimization entry;
no TypeCheck/lowering change, new IR/API/knob, scheduler or backend route.
The highest-value choice may change to a no-gain census if existing code already
elides this specific producer. Do not disable existing passes to make it fail.

The nested row-then-outer tree is a CONTROL, not another implementation target.
It must never be replaced by the flat graph. C02's host rewrite
(Optimize.fs8221-8390) matches an immediate top-level-host IRLet whose result
is the row apply. A row stage nested inside an outer IRReduce does not obviously
match that exact host shape before IRLift. Existing fuseRowFoldsModule6258
handles recursive-array row drains, not arbitrary scalar-map full folds; sibling
fold sharing's plainAxis6930/flat-leaf proof requires rank1. This makes observed
nested topology necessary, rather than claiming C02 covers composition.

## Eight minimal, unexecuted diagnostic sources

All source files live in C06-census-inputs. Every value-producing top-level
binding has an independent pin, including the required original input. The
failure control carries the load-bearing `(aborts)` header. These are source
assets for census, not added corpus tests or executed oracle output.

| File | Written graph and independent pin | Anticipated storage to inspect |
|---|---|---|
| 01-private-full | M3x3, map v*v+1; step(a,b)=2*a+b, ONE seed7; total7044 | required M9 cells; suspected private mapped9 cells; no row result |
| 02-nested-deferred | reduce(reduce(map,step,7),step,-3); rows84/213/468, total1206 | possible mapped9 and private rows3; neither presumed removed |
| 03-demanded-map-and-rows | named computed P and named rows printed; flat7044 and nested1206 | required M9 + P9 + rows3; preserve all prints/identities |
| 04-rank1-covered | x=[1,2,3], mapped[2,5,10], seed7; total84 | required x3 only, existing IRReduceCompute control |
| 05-fp-parenthesization | identity map, cancellation matrix; flat1 versus nested0 | graphs must remain distinct even if identity map storage already disappears |
| 06-producer-before-fold | map fails at final9; fold would fail at first mapped2 | retain producer materialization/failure order; totality refusal |
| 07-empty-axes | R0 and C0, full and nested graphs, literal seeds | fullR0=7, fullC0=7, nestedR0=-3, nestedC0=25, full negative-zero=-0 |
| 08-rowwise-covered-control | written deferred outer row map with inner rank1 fused folds, then outer rank1 fused fold | hypothesized required M9 only; no mapped matrix or row vector needed |

Exact integer reasoning for01: mapped row-major cells2,5,10,17,26,37,50,65,82;
seed7 accumulator sequence16,37,84,185,396,829,1708,3481,7044. For02/08, seed7
is restarted for each row:84,213,468. Outer seed-3 gives78,369,1206. These values
are exactly representable; a flattened substitute answers7044, not1206.

For05 under REPRO1/CONTRACToff: the flat sequence1e16,1,0,-1e16,1,0,0,0,0
loses the first1, cancels the large terms, and retains the later1. Separately
folded rows yield1e16,-1e16,0; the outer chain answers0. This specifically pins
floating-point parenthesization rather than treating addition as associative.
The rowwise spelling deliberately stays deferred; forcing its row vector would
manufacture a materialization absent from the best existing idiom.

Empty behavior is NOT guessed from flattening:01 has one seed and no step when
any axis is empty; nested R0 invokes no row body and returns outer seed-3;
nested C0 invokes three inner seeds7, so the outer chain is-3→1→9→25. The
negative-zero control is an empty full fold with explicit -0.0 seed. Seedless
and dynamic/ragged/compact cases are outside this pilot; retain their existing
inference/refusal behavior (TypeCheckInfer3355-3377,3830-3838,4027-4037).

06's existing source semantics force the anonymous mapped producer before the
serial full-fold body can read its first element. Its late_c06_producer error
must beat early_c06_fold. If an observed backend already violates that order,
stop and report a prerequisite defect; do not relax the pin or admit the map.
No new interpretation of a deferred producer is proposed.

## Topology hypotheses and proof boundary

01 should expose one host-local map producer p followed by a literal-seeded
scalar accumulator a and exactly two ascending IRForRange levels. The innermost
statement is the sole assignment a <- fold(a, p(r,c)); its final result is a.
Actual IRLet/unit-bind/cast/check nesting and all IDs must come from the probe.
Do not infer eligibility from `__rksrc`/`__rkacc` spellings or similar prefixes.
Casts, bounds/shape checks or other statements may not be discarded merely to
make a match. Prove redundant checks from the exact loop bounds/source axes,
otherwise retain/refuse. The two distinct nominal R/C axes both have extent3;
equal numbers do not license exchanging their provenance or traversal order.

02 should expose an inner partial-stage block/row apply consumed by an outer
IRReduce. 08 should instead expose outer IRReduceCompute whose row-valued input
is stored M and whose callable body contains the inner IRReduceCompute. These
are source-derived HYPOTHESES; stop on a different shape rather than expanding
an implementation during census. Full raw metadata is required to settle them.

A future01 proof must certify IDs/scopes, exact accumulator read/write and final
return, original fold argument order, seed site/count, map input/output axes via
sameIndex, exact ApplyInfo/MethodForInfo metadata, and no surviving producer,
row, alias, capture, key/index/shape, declared/embedded-type, print, return,
foreign-module or execution-side-payload demand. Only the actual matched host
occurrence gets an exemption. Complete whole-program foreign evidence should
use Lowering's existing with-census entry; low-level callers without it decline.

Use storedArrayProof plus full source-version/write/lifetime audit, not the
IsMutable flag of ordinary let bindings or generated names. Every callable and
transitive callee must be closed structurally total/pure, unstrategized and
resolved; conservative Effects.Unknown is not a purity certificate. Literal
seeds only. Fail/capture/unknown/structured/array-state/parallel/deferred-source,
multiple source or accumulator writes, hidden type/side payload, extra observer,
mutable alias and ownership/planner overlap all retain the existing path. Early
placement before IRLift/CSE/pool/hot-row/membership planning avoids stale side
facts. Do not broaden C02, C02-L1's eager array-argument barrier, C04 or A/D
ownership rules. No producer/consumer failure can be interleaved under this arm.

The intended numerical replacement for01 is one seed, then original map/fold
calls in exactly r0c0,r0c1,...,r2c2 order. It has scalar state only, not row
partials. For02/05/07/08 the written inner/outer seeds and row associations stay
separate. Demand03 stores its full observable P/rows. Original M stays required
in every rank2 case. A hypothetical9-cell mapped pool removal is72 payload bytes
in this tiny F64 fixture plus existing metadata; this is a storage MODEL, not a
measured peak/RSS/allocator saving. No traversal/predicate-sharing claim applies.

## Bounded sole-Luna topology/emission request

Root chooses a matching exact combined binary after reviewing this census and
input hashes. Reuse an existing matching DLL if possible; otherwise only root
queues a fresh build. Coder never executes this adapter or CLI. Use a private
worktree-root CWD, TEMP/TMP and fresh outputs under bench/.build/C06-census-runner;
record source/content/binary/tool hashes, environment, commands and full logs.
Set REPRO=1, CONTRACToff, FUSION=on, BLASoff, OMP_THREADS=1, LLVMoff and default
all-binding print policy. Other optimizer gates stay at their normal defaults.

First stage exactly21 invocations, no program/native/interpreter execution:

1. Canonical final lowering/IRValidate + full dump for all eight inputs (8 FSI).
2. Early public-prefix dumps for01,02,08 only (3 FSI). This is before Optimize,
   with the accepted C03/C04/C05 mono/HM/shape/zero/float/seed/binop recipe. It is
   single-module/provider-free and does not reproduce private preflight checks.
3. C++ source emission for all eight with FUSIONon (8 CLI emit).
4. C++ source emission for01/08 only with FUSION0 (2 CLI emit), then restore on.

Example FSI invocation (fresh output per input/stage):

```powershell
dotnet fsi --reference:bin/Release/net10.0/Blade.dll bench/notes/round4/C06-census-inputs/topology-probe.fsx bench/notes/round4/C06-census-inputs/01-private-full.blade early bench/.build/C06-census-runner/01-early
bin/Release/net10.0/Blade.exe emit bench/notes/round4/C06-census-inputs/01-private-full.blade
```

The adapter writes full typed payloads using the accepted untruncated SrcLoc-aware
C04 serializer; summaries print explicit needed fields/IRPrint, never `%A` of
full callable records. It runs no codegen/interpreter itself. In final mode it
uses Lowering.lower + validateIR; in early mode it dumps module records without
inventing a complete validated program. Unsupported collector payloads fail
explicitly and partial evidence must be retained. Stop on an unexpected lower/
emit/collector refusal and return to root before widening scope or rebuilding.

For emitted artifacts root/Luna must identify actual required M/x allocation,
private mapped/rows payloads and matching teardown, show the map/fold call inside
the original ascending nest, and count seed sites. Template declarations or
helper definitions are not allocation evidence. Verify source input prints and
all demanded03 prints remain. Preserve both map output axes and row result R
identity in full IR, including numeric-ID differences resolved through sameIndex.

Only AFTER this result root may request three real LLVM source emissions for
01/04/08 (optional3, total cap24), recording any unsupported renderer honestly.
No CLI plan, native run, interpreter test, category gate, full suite, timing or
measurement is requested at this census stage. Runtime value/failure pins are
independently reasoned future checks, not validated by source generation alone.
Root reviews actual early/final IR/dataflow before authorizing any implementation.

## Ownership and freeze

Owned files now: this note plus C06-census-inputs' eight sources, topology adapter
and SHA256SUMS manifest. No production/test/old-corpus or active queue edit.
If01 is selected later, likely owned implementation files are the existing
Optimize early terminal region and focused OptimizeTests/corpus only; lowering,
sibling sharing and backend/runtime seams remain outside the proposed card.
Inputs/census/adapter and relevant current source hashes are frozen in the
manifest. Its own hash is returned externally. Coder stops here for root review.
