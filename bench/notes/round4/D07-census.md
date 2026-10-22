# D07 — private statistical-barrier intermediate: admission census

Source/design only, at combined source `f9abed3bd9f6042656a750f2ccbb8ea6c963098a`.
No compiler, runtime, tests or policy surface changed; no compilation, emission,
execution or benchmark performed. D06 is an accepted protocol prerequisite
(256 checks passed, no failures, one Windows symlink-privilege skip), not an
existing compiler route. Read with D06-census.md, D06-protocol.md and D06-review.md;
the accepted protocol/review supersede the census's early ownership sketches.
Graft's query for the relevant compiler/protocol symbols returned no indexed
hits (68 indexed files, no savings footer); targeted F#/header reads supplied
the anchors below.

## Finding and stop point

There is a concrete missing route in `Optimize.fs`: a private computed array
survives a global scalar statistic and is then read by another scalar fold.
D06 can preserve that array's exact bits on disk. However, **there is no existing
user resource-grant surface to authorize this omission**. `IRProgram` contains
only `Modules` (`IR.fs:1832`), the whole-program emitter accepts only program and
name (`CodeGen.fs:3964`), and the CLI calls that API without resource policy
(`CliCommands.fs:308`). A targeted F# census finds no private-spool grant/quota
policy. D06's C++ `Grant` (`blade_private_spool.hpp:42`) is supplied by its native
harness; it is not user authorization propagated through the compiler.

The smallest next prerequisite is an explicit typed optional resource policy
and its documented artifact/run scope, described below for approval. Do not
implement a disk route merely because the header is deployed, a temporary
directory exists, a budget threshold is crossed, or a compiler environment
variable is set. This census stops before policy or compiler implementation.

## One proposed source idiom and typed region

Use the existing language, with print selection restricted to `result`:

```blade
import zarr as z
let s = z.load("fixture.zarr")
let A = s.vars.A |> z.stream
let p = method_for(A) <@> lambda(x) -> x * x + 1.0 |> compute
let t = reduce(p, (+), 5.0)
let m = t / Float64(extents(p))
let result = reduce(method_for(p) <@> lambda(v) -> (v - m) * (v - m), (+), 3.0)
```

This is the section 5.2/ST09/S4 graph in
`bench/notes/peakmem-streaming-optimizer-plan.md:272`, with an explicit first-fold
binding to keep the initial recognizer small. It is an authored diagnostic
witness, not a freshly observed lowered/emitted artifact. Component constructs
already exist; Luna should capture this exact combined graph before a future
implementation freezes admission tests. Do not silently widen a recognizer if
normalization changes this spelling.

The canonical candidate, after specialization/binop lowering and inline lifting,
is four ordered, immutable, contiguous top-level bindings:

| Binding | Required IR and type |
| --- | --- |
| `p` | `IRCompute(IRApplyCombinator pinfo)`; one direct `IRVar A` operand; ordinary dense plain rank-one nonvirtual Float64 result, same static axis/extent as A; scalar Float64 kernel `x*x+1` |
| `t` | `IRReduce(IRVar p, add64, Some literal64Seed)`; scalar Float64 |
| `m` | Float64 division of `IRVar t` by the statically resolved Float64 extent of p; no new array read/effect; scalar Float64 |
| `result` | `IRReduceCompute(IRApplyCombinator rinfo, add64, literal64Seed)`; one direct `IRVar p` operand and scalar Float64 transform kernel; its sole nonparameter scalar dependency is immutable dominating m |

`IR.fs:203,221,495` define those forms. `Lowering.fs:974` distinguishes a stored
array reduction from the fused deferred-map terminal. `TExprCompute` lowering
(`Lowering.fs:836`) retains the materialization wrapper for an apply. Named
scalar addition kernels must resolve to the exact expected body/type, not be
recognized by source name. The transform's m may be represented by a lifted
capture/parameter; certify the actual specialized callable, final arguments and
capture identities. The first card can decline other equivalent IR shapes.

Initial provider boundary: one local Zarr v2/v3, identity codec, disk f8 -> F64,
ordinary unmasked/unwindowed `.stream`, plain dense rank one, matching static
N >= 0 and positive chunk capacity C. Existing `rank1TerminalChunk`
(`providers/ZarrProvider.fs:2202`) supplies much of this metadata capability,
including its current representability limits, but its terminal missing-value
semantics must NOT select the reader. Initially set spool B=C. No packed,
Icechunk, NetCDF, multifile, compressed, conversion, allow-missing, multiple
source, halo, shifted or irregular case is needed. Do not reread A at replay.

## Existing proof seams and the new proof obligation

`Optimize.storedArrayProof` (`Optimize.fs:3700`) positively identifies p's
`IRCompute` storage at its original site. This establishes the exact resident
intermediate being removed; it does not authorize removal. `planPoolReuse`
cannot donate p while the second consumer remains live. The rank-one
`planHotRowFolds`/cell-epilogue recognizer (`Optimize.fs:3156-3245`) finds a related
producer/fold but is not a statistical-barrier spool plan and retains storage.

Add any eventual planner in **Optimize**, taking the final whole program and
explicit request/print demand. Keep IR semantics available to the interpreter;
an immutable backend plan may replace only this certified storage schedule.
Run recognition after semantic normalization, inline lifting and CSE, then
certify/decline ownership conflicts with existing pool, hot-row, cell-fold,
epilogue, membership, compaction, compact-row, orbit/tile and lifetime plans.
Do not insert omission ahead of a later schedule mutation without revalidation.
`Lowering.fs:3178-3206` shows the current scheduling order; its earlier
`foreignRefs` shortcut is conditional on compounds and must not be reused as a
complete private-spool foreign-demand proof.

Use `bindingPayload`/`bindingSideOperands` plus the shared
`compactionRoots` (`Optimize.fs:3637`) and `moduleReferencedIds` (`:3674`)
machinery. It includes module type payloads, embedded extents, callable bodies,
parameter/return/capture types, provider-write source IDs and masked-read names.
Resolve name roots too: an ID-only reference set is insufficient. Census all
modules, or initially require exactly one module using explicit whole-program
provenance. Reject every p alias, capture/escape, return, mutation, demanded
print, other consumer, metadata/type read not reduced to a checked scalar
constant, and hidden side-table use outside the certified region. Source A
must have only this traversal. Numeric IDs are not statement order: use binding
positions for dominance and exact region order.

`CodeGen.fs:3058` makes computed p printable by default. Therefore the ordinary
unselected/default-print program **must retain p**. Demand p/t/m for printing
also declines the first narrow route. Only the selected result is published.
No required stored/printed output may disappear. An unprinted immutable N
constant can be a certified dependency; arbitrary intervening bindings decline.

Reuse `IRTotality.auditApply` (`IRTotality.fs:310`) and a stricter closed scalar
arithmetic/capture check. Float `+ - * /` is total IEEE arithmetic (including
NaN/Inf and zero division); provider operations are not. Reject writes, mutable
captures, unknown calls, recursion, float-to-int casts, integer division,
unproven indexing, RNG, I/O, diagnostics, pool-scope/release nodes and other
control/lifetime boundaries. Treat producer, first seed/fold, barrier, transform
and second seed/fold separately. Preserve ordinary arithmetic renderers and
all required prelude/statement fields; extracting only `CppExpr` is not proof.

Require the two original ascending serial seeded chains. Decline licensed
reassociated/parallel modes unless reproducible policy forces the existing
serial association; initially no OMP/CUDA/MPI/Reynolds, epilogue, tuple fold,
unseeded fold, reduction join or block partial sum. Pin contraction off in
bitwise tests and keep the same contraction setting in both variants. Each
produced cell must first be stored in a Float64 scalar/buffer slot; both spool
and first accumulator consume those same rounded bits. Replay consumes that
slot representation, never re-evaluates x*x+1. m is evaluated exactly once
after all production, then the second chain starts at its original seed.

The plan/capability must name the exact source and four binding IDs, types,
resolved kernels/captures, print demand, policy and original payloads. Validate
identity against the final emitted graph, and refuse stale/colliding plans.
Pass it explicitly in `CodeGenContext` only for its authorized top-level
binding/region; nested bindings and generic/public low-level emitters get none.
D01's `StreamTerminalModule` and D02's `TopLevelProviderRead`
(`CodeGenState.fs:127-131`, defaults `:1820`) are provenance examples, not a
spool permission. Reset on each assembly and do not use an ambient grant cell.

## Source order, new faults and publication

The original producer currently goes through the elementwise segment/window
path (`CodeGenCuda.fs:3550-3757,4256-4320`). Each chunk-sized window is assembled
and validated BEFORE its cells execute. `GenStreamRows`
(`providers/ZarrProvider.fs:2234`) calls the normal window core; the None arm
at `:2180-2194` scans the window with global origin. A later short/null chunk
inside that window precedes its missing-value scan; an earlier invalid window
precedes reads of later windows. D01's whole-read tally
(`genStreamRowsTerminal`, `:2225`) deliberately has different precedence and
cannot be substituted here. Keep source open/probe placement and exactly the
same window calls, clamps, order and diagnostic coordinates.

Resource/shape/policy denial can be an explicitly documented pre-execution
refusal, before source consumption; OOM behavior is not promised identical.
Once source consumption begins, a spool protocol fault must not hide an
original later provider failure. Latch the FIRST create/append/seal failure,
disable writes, retain the allocated input/decode capacity and execute the
remaining original window reads/validations in order. Producer/stat arithmetic
is certified total and can be skipped during fault drain; no result is
published. A provider's existing std::exit/panic remains primary and triggers
D06's checked exit cleanup. If all source windows succeed, report the latched
spool fault at the original Blade span with separate cleanup status. No
automatic retry/recomputation/resident fallback after partial consumption.
If allocation/setup cannot guarantee this drain capacity before the first
read, decline rather than claim precedence. This fault-drain call-site protocol
is NEW work: D06 returns statuses but does not itself drain providers.

After successful production/seal, evaluate m, open the unique reader, validate
each block before transforming it, and fold in the original global order.
Require the extra terminal read that validates footer/EOF, then reader finish
and scratch retirement BEFORE assigning/printing the public terminal. An
integrity/read/close/cleanup fault refuses publication. Generated diagnostic
calls carry source spans; the header still calls no panic. Preserve primary
and secondary cleanup outcomes, including D06's post-success final-close error
case where scratch may already be absent. Do not invent all-program stdout
transactionality or sweep old run directories. Recovery stays explicit under
the D06 ownership proof; no cache/source snapshot/immutability assumption.

## Smallest resource-policy/API prerequisite for approval

Recommend one optional **typed policy object**, default None, supplied by the
invoking compiler/run host under the user's explicit resource policy: absolute root, tracked
workspace allowance, logical serialized disk allowance and block-cell cap.
Compiler derives N, F64 format and axis identity from the certified graph;
users cannot override descriptor identity. Reject invalid/overflowing fields
before source I/O. One active region only. Root must review the policy's
scope and wiring before code: this is a proposed API, no existing flag.

The end-to-end contract must be:

1. An explicit resource policy reaches the compiler/run host; this does not
   require repeated human reauthorization. User authorization and preferences
   persist. Absence, malformed policy or unsupported
   Windows-local-ASCII capability grants nothing. Pool retention
   (`Toolchain.fs:216-270`), revision tile caching and CLI compiler-build scratch
   (`CliCommands.fs:437`) are unrelated and cannot imply permission.
2. Pass this immutable policy to an options-bearing whole-program planning/
   emission entry, with existing public APIs retaining None. Optimize returns
   a complete admission or a reason to retain the original graph, never a
   guessed root/budget. No static environment cache or implicit OOM spill.
3. Choose and document ONE policy delivery design. A **policy-bound artifact**
   deliberately embeds the supplied policy and continues to use those declared
   root/limits when executed; persistent disk behavior must be explicit in its
   artifact contract. A **run-only transport** instead supplies policy at
   execution and needs a bounded launch/parser contract, including behavior
   when policy is absent. Neither design is already implemented. The plan
   does not require launch tokens, a mismatch handshake or renewed consent
   before every execution. Cache identity must include the complete embedded
   policy (or the run-transport schema and relevant plan inputs) so a default
   resident request cannot accidentally reuse a policy-bound disk artifact.
   Existing emit/compile/REPL/notebook calls without typed policy retain the
   resident route; a future explicit options-bearing call may bind policy.
4. At runtime check aggregate workspace capacity and the platform/root, create
   the accepted D06 typed Grant, register checked cleanup, and acquire one
   writer before source reads. Reserve full logical disk bytes up front;
   filesystem free space is not a reservation. Normal cleanup/exit/recovery
   retain D06's owner sidecar, fixed filenames, verified handles and uncertainty
   refusals. No public provider sink expansion is involved.

This suggests a small typed-policy prerequisite BEFORE the compiler card;
it does not justify choosing new Blade syntax, CLI names, JSON schema,
environment variables or toolchain defaults in this census. An approved host
API can carry an explicit user policy directly; a test-only native Grant alone
does not establish that compiler contract. Missing policy means ordinary
resident execution, not a newly spanned refusal of valid existing programs.

Recommend the minimal **API-first, policy-bound artifact** design: add typed
options to an explicitly named whole-program emission entry, validate and
embed that policy, and document its persistent execution behavior. Existing
callers stay on the current API with None. This needs no new Blade syntax,
launch parser or runtime reauthorization mechanism, and uses D06's existing
typed Grant at the generated call site. It exposes an embedding API first,
not yet an ordinary `blade run` option. A later CLI surface needs bounded
field/path parsing, help/error handling, option propagation and executable-
cache integration; it can choose artifact binding or run-only transport using
the same typed policy, not a second policy engine. Neither choice needs a
general spill selector, shared-root quota service or recovery command.

Recommendation: keep compiler admission explicitly deferred after D06 until
root reviews the typed policy/artifact and accounting contract. The complete
CLI-to-spool route does not fit an honest single kernel change today: its new
policy boundary and fault-drain protocol are independent correctness work.
A separately reviewed minimal typed API prerequisite can then leave one
bounded kernel card for this exact four-binding region. An internal opt-in
compiler test alone must not be reported as the user-facing feature. The user
has authorized this round; this is a design checkpoint for root, not a request
for another human permission prompt.

## Capacity and metadata ledger

Removed required intermediate: p's 8*N resident bytes. Final output is one
scalar; do not add an array to make this example look harder. Payload workspace
with B=C is converted input 8*C + padded disk decode 8*C + spool/replay block
8*C, with phase reuse possible only after proof. Conservatively budget all
three plus D06's `sizeof(Control)+4096`, scalar states, fixed provider stream
buffers/handles and any generated owner allocations. D06's `memory_bytes`
checks ONLY its own workspace (`blade_private_spool.hpp:139-150`); passing the
whole pipeline allowance unchanged is insufficient. The compiler must check
the aggregate ledger and give the spool its remaining allowance. Provider
ifstream buffers need an explicit bounded accounting/capability, or describe
the allowance as tracked buffers rather than a hard bound on all allocations.
No RSS/page-cache or total-compiler-memory promise follows.

D06 logical disk high water is checked
`72 + 8*N + 64*G + 64 + 128`, G=N/B+(N%B!=0); the last term is the root owner
sidecar. Directory/allocation metadata is excluded. Exact IEEE bits, block
origin/sequence/count/CRC, footer and EOF are already protocol checks. At most
four spool native handles, PLUS the provider's bounded simultaneous handles
and standard output/runtime facilities; four is not a whole-program bound.

The existing elementwise path eagerly builds `runs` as a list
(`CodeGenCuda.fs:3663`, then emits ExplicitRuns). Do NOT inherit that O(N/C)
metadata in a claimed bounded route. Use a local checked arithmetic cursor
with rawLo=next, step=min(C,N-next), rawHi=rawLo+step and next=rawHi, issuing the
identical normal windows; this is D05's compact uniform traversal principle,
not a halo/cache change. N=5,C=2 gives [0,2),[2,4),[4,5); N=4,C=2 gives
[0,2),[2,4); N=0 gives none. All sums/products checked wide before narrowing,
step>0 for every executed iteration; no N+C-1 formula. Spool metadata is
constant, not a per-block catalog. Zarr `storeFiles`/fingerprinting
(`providers/ZarrProvider.fs:1272-1299`) still enumerates source files; explicitly
exclude that from any bounded end-to-end compiler claim.

## Proposed bounded diagnostic and sole-Luna gates

First request ONLY after root approves the policy design: lower and emit the
exact N=5,C=2 witness with selected result, record actual typed IR, final schedule
ownership and ordinary source windows, without initially claiming a spool
route. If the expected canonical form is absent, return that evidence for a
scope decision. No new source was executed for this census.

A later implementation should author assertions before Luna runs them:

- Explicit opt-in produces a real `private-statistic-spool-v1` admission and
  exact writer/append/seal/reader/final-EOF/finish calls. Inspect emitted output
  for NO p Array declaration/pool/8*N allocation/copy, NO second provider read,
  NO run-boundary table, and no premature result publication. Confirm actual
  payload counters 8*N written/read and bounded capacities/disk high water;
  a marker alone is insufficient.
- Independent values: x=[1,2,3,4,5] -> p=[2,5,10,17,26], t=65, m=13,
  transformed [121,64,9,16,169], result=382. N=1,x=1 -> t=7,m=7,result=28.
  Metadata/synthetic zero extent -> t=5,m=+Inf,result=3, with header/footer-only
  spool, no provider chunk reads; do not expand unsupported frontend zero
  syntax. Odd tail, finite fill and exact replay of signed zero/NaN payload
  bits belong at their distinct provider/protocol layers. No forced payload
  execution of huge shapes.
- Baseline no grant/default-print/demanded p retains resident allocation and
  values. Decline extra consumer/alias, cross-module hidden demand, callable
  capture/type/extent/side operand, mutation/effect, wrong rank/type/provider,
  mismatched extent, unseeded/nonadditive/reassociated/threaded fold, other
  storage owner and stale graph/capability. Direct low-level emitters and a
  later no-grant invocation must not inherit permission.
- Fault precedence: early spool write fault + later short chunk/null missing
  chunk -> original provider error after the same drain windows; early spool
  fault + later invalid-value window -> original invalid-value error. Within
  one window short/null later chunk beats an earlier missing value; in distinct
  windows the earlier invalid window beats a later short chunk. Source-valid
  write/seal/flush/close failure -> first spool error, no result. Replay
  corruption/truncation/footer/EOF and cleanup failure -> no result; secondary
  cleanup failure preserves primary provider diagnostic and owner evidence.
  Use D06 small injected failures, never real disk exhaustion.
- Invalid policy/schema, quota/overflow/unsupported platform, hook exhaustion,
  bounded path/root substitution and exit/panic cleanup retain protocol
  protections. Existing D06 recovery harness is already accepted; rerun only
  if its production contract changes, not as a substitute for compiler gates.

Expected future focused runner request: one Release build, then the authored
small private-spool admission block plus standalone Zarr tests affected by the
normal reader, in private scratch directories; no whole suite or benchmarks.
Report the existing optional internal NetCDF differential skip separately and
any Windows symlink-privilege skip if that probe is actually run. Unsupported
platforms cannot count as successful native disk-route validation. Root selects
the exact gate after policy and source review.

Proposed future files: a small typed policy/options boundary (location depends
on approval), Optimize planner, explicit CodeGenState/CodeGen whole-program
capability, one narrow CodeGenBinding/Cuda region emitter using existing normal
Zarr reader, targeted Zarr assertions and docs. No public sink/provider rewrite,
cache/prefetch/source replay, general external operator or frontend language
construct is part of this card. **Await root review of the missing typed policy/
artifact prerequisite; implement nothing in this D07 census checkpoint.**
