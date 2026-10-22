# C05 source census: PM58 private sparse wildcard scalar terminal

Source-only checkpoint, lane HEAD `1331b1786ca9aba116868f77bb9f8b4c6ae224bc`.
Read current integration sources/docs (integration HEAD 4eadbdc7 at first read),
not an old plan snapshot. No production edit, build, lowering, emission,
interpreter, execution, test, oracle, benchmark, commit or merge was performed
by this coder. The existing `.r4tmp` directory is preserved. Graft returned no
F# hits; the following are targeted source reads, not executed observations.

## One genuinely missing route

Propose **one ordinary top-level Float64 scalar fold whose direct anonymous
operand is a rank2 sparse parent's partial read with one free axis**. Parent
is already stored, keys are static and insertion-ordered, payload has no trailing
dimensions, pin is a nonnegative literal, seed is an exact Float64 literal,
and the binary fold is closed, total, serial/repro. No named-residual join,
partial-fold array result, generic sparse transformation, new sort/hash scheme
or scalar-map fusion is proposed.

Public syntax already exists: index-types/176_sparse_partial_scattered.blade
uses `S((_,1))`; 177 covers a rank2 residual sparse and chained reads; 183/184
cover `sparse(values, keys)` with static/runtime keys. index-types/175 pins the
given key order with an order-sensitive fold. Most directly,
memfree/024_inline_sparse_gather.blade already uses
`reduce(F((0,_)), (+))` at module scope and in functions/kernel bodies.

Existing support **binds and frees one gather; it does not remove it**:

| Current source | What it establishes |
| --- | --- |
| CodeGenExpr.fs:20-70,1040-1100 | `GatherTeardown` classifies allocating sparse partial reads and pairs each helper with its teardown. |
| CodeGenExpr.fs:1422-1431,1643-1669 | Expression fold/scalar consumers bind an inline gather once in an IIFE and free it. |
| CodeGenBinding.fs:4066-4082 | Statement fold first emits `auto ...__gath = <gather>`, recursively folds the named array, then deallocates it. |
| cpp/nested_array_types.hpp:244-266 | Rank1 residual builds `vector<size_t> ranks`, copies selected doubles into `new T[max(m,1)]`, and allocates `new size_t[1]` for match count. |
| cpp/nested_array_types.hpp:208-237 | Rank>=2 residual additionally builds subkeys and a new sparse index/hash; outside this pilot. |

No source arm in Optimize specializes this wildcard fold. A primary source
still statically reaches the unconditional `GatherTeardown` statement arm.
This is strong source evidence for a missing allocation-elision route, **not
an observed IR/emission admission or a measured memory saving**.

For the proposed rank1 residual, do not claim removal of a residual hash/key
table: it has none. The removable storage is its ordinal vector (capacity may
exceed m), copied double buffer (at least one cell even for no matches), and
one-element extent buffer. Parent sparse key/hash tables, parent compact
values, original printed vals and the scalar result remain required. Target
is one scan of parent keys plus one accumulator, without the gather vector,
copy pass or residual fold-input buffer. It is neither sharing multiple
terminals nor reducing predicate count below one key test per parent entry.

## Order and existing semantic/ownership contract

Canonical formalism.md:674-681,760-769 specifies supplied key order, duplicate
key refusal and a partial read as a copied residual in that same order.
cpp/index_types.h:510-550 stores `rank_to_tuple` from the supplied vector and
builds a separate lookup hash; hash enumeration does not choose fold order.
The dense gather scans parent ordinals `r=0..cardinality-1`, appends matching
ranks in that order, then copies parent.data[r*trailing_stride].

Interpreter Loops.fs:2683-2693 evaluates pinned expressions in axis order,
then ArrayOps.sparsePartial:1846-1885 scans Keys in ascending parent ordinal
and constructs the same dense values. Loops.fs:2352-2365 evaluates/forces the
operand BEFORE evaluating the seed. ArrayOps.reduceArray:849-864 starts at
the explicit seed and calls `fold(acc, value)` over every value ascending.
No matches returns the exact seed, including -0.0. A false key test must skip
the fold call, not supply a numeric zero. Neither sorted free coordinates nor
tuple hash order is equivalent.

`sparse(vals,K)` lowering is a **side-table constructor**:
Lowering.fs:2495-2513 creates a unit placeholder binding and records vals in
SparseInits; its keys ride the parent's typed IRSparseKeys extent.
CodeGenBinding.fs:3067-3112 constructs the index, validates values/key length
(BL8001), copies values in key order, and registers `deallocate_sparse` for
both parent values and index. Interpreter Loops.fs:1786-1822 mirrors that
constructor. The selected terminal must keep this complete constructor and
its lifetime/validation at the source site, not treat the placeholder as the
parent's execution or borrow vals as if it were the sparse value snapshot.

The current shared `Optimize.storedArrayProof`:3700-3754 deliberately refuses
SparseInits as an unproved side-table value. This must be explicit: a pilot
using the source below needs a **local proof of exactly this known sparse
constructor**, including a dominating stored rank1 Float64 vals literal,
static distinct valid rank2 keys, matching five-cell geometry and the actual
SparseInits payload. Do not broaden storedArrayProof globally or assume a
source is stored merely because its binding has an array type/unit value.
Computed/deferred/runtime-key/aliased/foreign parent variants stay outside
the first implementation; they are not additional cards silently included.

## Topology hypothesis and smallest implementation proposal

For 01, the early hypothesis is:

```
S: Array<Float64 like SparseIdx<K>>, Value=unit, SparseInits[S]=Var(vals)
total: Float64 = IRReduce(
    IRIndex(IRVar(S), [IRTuple [IRLitUnit; IRLitInt 1]], ...),
    resolved_binary_fold, Some(IRLitFloat 7.0))
```

`IR.classifyCompoundIndexTuple`:3000-3036 supplies pinned/free positions from
the wildcard sentinel. `IR.typeOf`:3144-3193 makes the one-free-axis residual
plain rank1 with `IRCompoundProject(parent,pinCount)` dynamic extent; do not
pretend it has the free coordinate's maximum extent or erase that type payload
from demand analysis. IRLift.fs:550-574,788-793 appears to retain the direct
partial IRIndex as a fold operand. These are hypotheses until the runner
confirms early AND final IR. A source-level local let, CSE or another shape
must trigger review, not broaden the matcher or assume generated names prove
privacy. The authored probe stops if 01 is not this exact direct topology.

The smallest proposed production change is a **C++ statement-fold arm at the
existing genReduceBinding/GatherTeardown boundary**, before its allocating
fallback. A closed proof query consumes the current complete merged module,
actual ordinary host ID/IRReduce expression and source ID; it certifies this
single anonymous operand, known parent constructor and whole residual/root
census. It returns a proof object, not an alternate sparse container. The
renderer emits a scalar literal accumulator and `for r < S.idx->cardinality`
over `S.idx->rank_to_tuple[r]`; when the literal pin matches, it calls the
existing resolved fold wrapper with `(acc,S.data[r])` in that order. Seed is
evaluated once at the original consumer boundary. No array, ranks, index,
sort or new runtime helper is needed. The original scalar binding ID/name/type
and print policy stay exact. No OMP/SIMD/reassociation licence is introduced.
First arm is repro-only with existing per-call BLADE_FUSION and FP gates.

This leaves semantic IR and the interpreter's canonical gather intact; compare
its independent result against the emitted route. It is honestly a C++ storage
elision, not a new shared-IR or LLVM route. Existing expression/function-body
and named-residual paths retain their ownership/teardown code. No metadata
nodes are deleted from IR, and no generic sibling/compaction pass is expanded.

There is an existing complete-program emission seam: CodeGen.fs:2168,2311
sets ctx.StreamTerminalModule only for whole-program callers, otherwise None;
the complete merged IR is already available there. Reusing its **evidence**
for a pure proof query needs root/D-lane review because this field documents
the provider terminal today. No new ambient table, cached environment flag,
provider admission change or public optimizeModule signature is proposed.
Without complete evidence (including low-level genModule callers), decline.
If root prefers an early IR rewrite, representation must be designed separately:
C01's value-only guarded fold does not expose sparse key coordinates, and
manufacturing a dense predicate/value map would merely move the allocation.
This census does not authorize either design automatically.

Proposed owned files after root/topology approval: Optimize.fs (pure proof
query reusing existing census/write/IRTotality helpers, no new optimizer pass),
CodeGenBinding.fs (one statement consumer arm), focused OptimizeTests and
sql-reduce corpus/handoff. A field-documentation/seam edit in CodeGenState.fs
requires coordination with D first. No index_types/nested_array runtime,
LLVM, interpreter or sparse algorithm redesign is needed for this pilot.

## Closed census and refusal obligations

- Require exact host root ownership, direct single operand and no hidden
  shape/key/extent/type-payload observer. Walk declared and embedded types,
  functions/captures/signatures, binding values AND side-table payloads,
  foreign roots, print/return observers, lifetime events and all planner roots.
  Exempt only the actual host occurrence by host ID, never any reference-equal
  expression in another binding. Named/printed residual, residual alias/keyed
  lookup/capture/escape, repeated residual or additional terminal declines.
- Parent storage remains; ordinary parent reads are not residual demand.
  Still require one original source version: reject mutation/rebinding through
  the parent, values/keys provenance chain, aliases, mut parameters, captured
  or opaque/transitive calls, side-table calls/payloads and unknown writers.
  All scheduling facts/capacity/lifetime roots stay visible. Conservatively
  refuse overlapping planner candidates unless disjointness is demonstrated.
- Allow only the known single SparseInits constructor; reject other execution
  side payloads, provider/random/read/write roots, runtime key builders,
  deferred/computed parents, unknown source aliases, trailing payload axes,
  structured elements, multiple pins/free axes and parallel/strategy metadata.
  Parent duplicate/length/key validation must finish at the original site.
- Use the shared closed scalar totality proof on the resolved fold and all
  transitive callees, with no captures/opaque calls. Failing/unknown fold,
  nonliteral/failing/captured seed or pinned expression, unseeded BL8003 fold
  and licensed non-repro arithmetic retain the existing materialized route.
  Removing gathers must not reorder a parent/coordinate failure with a fold.
- Preserve result reuse: any number of later reads of the same scalar total
  is safe; do not repeat the parent scan when a scalar result is reused.
  Test alias/capture/type-only/foreign/side-payload observers synthetically
  after topology is established, not just source text/name checks.

## Frozen diagnostic inputs and bounded Luna request

C05-census-inputs contains six valid-looking, **unexecuted** sources. Analytic
pins: 01 [4,2,3] in key order gives 7423 (sorted order gives wrong 7324);
02 observes residual [4,2,3], first4/count3/total7423; 03 gathers privately
then mutates its mut parent to99 and must retain snapshot total7423/after99;
04's final parent cell fails in late_sparse_parent before any early_sparse_fold;
05 has no matches and must return exact -0.0; 06 matches every key and gives
749283. No executed oracle/pin generation took place. Source validity, exact
mut-sparse parameter handling and emitted allocation counts await the probe.

Root reviews/freezes this census and hashes before any probe/implementation.
Luna alone selects a fresh exact combined DLL/binary and records its revision,
SHA256, tools, environment and isolated output directory. Set REPRO1,
CONTRACToff, BLASoff, OMP_THREADS1, FUSIONon, PRINT unset, LLVMoff; clear stale
tile/stream policy flags. No build is needed if root's current exact combined
binary exists. From the private worktree, for each of the six source files:

```powershell
dotnet fsi --reference:<absolute-combined-Blade.dll> --exec bench/notes/round4/C05-census-inputs/topology-probe.fsx <absolute-input> early
dotnet fsi --reference:<absolute-combined-Blade.dll> --exec bench/notes/round4/C05-census-inputs/topology-probe.fsx <absolute-input> final
<absolute-Blade.exe> emit <absolute-input>
<absolute-Blade.exe> plan <absolute-input> --json
```

Capture each invocation's full stdout/stderr/status and actual emitted .cpp.
This is twelve bounded IR-prefix/full-lowering probes plus six CPP emissions
and six plans, **no program execution**. The early adapter copies the working
C03/C04 public-prefix recipe; it is single-module/provider-free only and does
not reproduce private preflight checks. The final arm uses lowerCaptured and
validates its full pipeline result. Diagnostics are separate from any future
optimize/native witness totals.

The diagnostic's static robustness review replaced whole-callable `%A` prints
with explicit flags/groups/effects, each parameter/capture's identity and full
type payload, return type payload, and `IRPrint.ppIRExpr` for the body/fold.
This retains capture/signature/arrow metadata without a generic callable/body
serializer encountering SrcLoc. The accepted C04 collector's SrcLoc.Span
repair was inspected; no new reflection walker is introduced here. Stored-proof
results print only the reason/chain IDs, never entire binding records. The six
sources and the 24-call request are unchanged. This is a static edit, not an
observed formatting failure or an executed probe.

For 01 require direct topology or STOP for root; record actual numeric IDs,
source/operand types, SparseInits, fold params/body/captures, seed, source and
host scopes. Inspect real C++: one gather call, one ranks vector inside the
helper, one residual values/extent allocation path, exact ascending parent
ordinal gather and fold, matching teardown, parent constructor/index/values
still retained. 02/03 must preserve residual storage and snapshot ownership;
04 must not splice the producer into the terminal. 05/06 expose empty/all-match
storage paths. Do not treat the old bind-once fix or unchanged fallback as new
optimization. No optimizer-off matrix is needed at this unimplemented census.

One additional primary-only LLVM emission may record the current honest
capability refusal: EmitLlvm.fs:8033 rejects SparseInits before expression
rendering; static-key sparse ranges also refuse at 6077-6082. An attempted
LLVM emission that falls back to C++ is NOT LLVM support. No expansion of the
sparse renderer is requested. Future implementation validation must include
independent interpreter/native seed/order/snapshot/failure pins, ON/OFF emitted
route assertions (including removal of gather helper/teardown at this host),
actual C++ execution provenance and explicit unsupported LLVM disposition.
No full/default suite, general matrix, sanitizer or measurement run is requested.

File hashes are frozen in C05-census-inputs/SHA256SUMS.txt; this note's hash is
reported externally to avoid self-hashing. Stop after census/root review.
