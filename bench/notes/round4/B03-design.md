# B03 design checkpoint: C++ scalar-pool capacity ledger

**Design approved by root; one implementation authored, unexecuted, awaiting diff review.** 2026-10-22.
Lane checkout `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`,
base `2eb6c0ad` (B02; integration counterpart `b4ac861e`). The initial design-only checkpoint
was approved before runtime edits. Implementation and runner requests are recorded in
[B03-handoff.md](B03-handoff.md); no compilation, execution, benchmark or passing test
evidence is supplied by the coder.

Scope: one opt-in diagnostic ledger at the existing C++ `blade_pool` API, plus the
[standalone probe specification](../../diagnostics/pool-capacity/spec.md). No allocator
retention policy, compiler optimization, last-use release or LLVM change is proposed.

## 1. Source census and consequences

Graft was queried first; it indexes no `src/cpp` nodes. Targeted source inspection found:

| source in this lane base | fact / design consequence |
|---|---|
| `src/cpp/nested_array_utilities.hpp:81`–103 | Existing Stats are process-wide **event counts**. Eligible `fresh` increments before native allocation and can include failure. Preserve these counts and their existing stderr record unchanged; do not relabel them successful allocations or bytes. |
| same file:137–148 | Eligibility avoids multiplying a huge request; cache-off alloc/release route directly to typed new[]/delete[]. New accounting must also cover small, oversized, non-arithmetic and cache-off calls through the API, not only eligible cache events. |
| same file:151–175 | Typed deleters ride slots; caches are per-thread. Eviction and explicit/thread-exit flush delete retained arrays. Do not change their type, policy, order or owner thread. |
| same file:177–202 | Exact type/count cache hits transfer ownership. On an eligible miss with retained entries, native bad_alloc triggers a local flush and one retry. Failed attempts add no successful element capacity. |
| same file:204–220 | Release can retain in the **releasing** thread's cache. After its Reaper marks the cache dead, later release deletes directly. Native operations and TLS destructor registration can invoke other runtime code. |
| same file:445–452, 541–547 | allocate / allocate_strict pass `max(total,1)` actual cells to alloc. Empty payload is not necessarily zero physical element capacity. Pointer skeletons use separate direct allocations. |
| same file:604–626, 661–674 | Rank-one zero extent releases its one-cell sentinel. Rank>1 zero-leaf teardown explicitly cannot recover the sentinel and intentionally leaves it allocated. Count it; **never forge closure** at flush/thread exit. |
| same file:611–612, 667–668 | `extents=nullptr` can call direct delete[] with unknown count. Future instrumentation must emit an explicit **unknown-release** completeness failure at these known bypasses, or individually prove a caller cannot reach them. This design chooses invalidation. |
| `src/cpp/alloc_layout_tests.cpp:405`–535 | Existing cache tests already exercise typed/exact-size reuse, budget/slot eviction, flush and thread exit using legacy counts/native arrays. Reuse their policy conventions; do not claim those counts already measure capacity. |
| `tests/AllocTests.fs:17`–100 | Existing allocation block compiles several retention policies. This pilot is a separate bounded standalone probe; do not silently broaden the test block or launch a full suite. |

Census identity (raw working-tree SHA-256):

- `nested_array_utilities.hpp`: `A025809409B03DF585E08324810A60CD3E55DFE2583A12DB97C36362D056E4A5`
- `alloc_layout_tests.cpp`: `163BE22498F0062F0959799C7ACA3D2C14A1DB1378402CA71473684ED4522920`
- `tests/AllocTests.fs`: `8C711E37B005E8A1603FDC8D422EEB747B6B5A6820A8188DA60DDBA176D28D66`

## 2. Precisely what is counted

Unit: **element-slot capacity bytes**, `actual_cells_passed_to_alloc * sizeof(T)`.
This includes C++ object representation/padding and allocate's empty sentinel when
present. It excludes new[] cookies, alignment slack, allocator size-class slack, allocator
metadata, stack, TLS objects and the diagnostic ledger itself. It is not usable heap
size, logical value bytes, semantic liveness, residency, commit or DRAM traffic.

Track all successful `blade_pool::alloc<T>` calls, including non-arithmetic T that is
never cached, and their matching API release/deletion events. Constructor-owned heap
allocations outside this API remain excluded. A direct `alloc<T>(0)` success has zero
element bytes but one active block; allocate's transformed empty request has one cell.

Current process-wide quantities:

- `active_element_capacity_bytes` / `active_blocks`: successful capacity handed to an
  API caller and not yet transferred to a cache or completed its native direct delete.
  This is **allocator ownership**, not logical liveness. An unreachable sentinel or a
  scalar pool stranded by a later skeleton-construction failure remains active.
- `retained_element_capacity_bytes` / `retained_blocks`: capacity owned by any thread's
  cache and not yet reused or completed native eviction deletion. Its source cells need
  not contain demanded/live values.
- `combined_element_capacity_bytes = active + retained`, from the **same locked state**.

No pointer/ownership registry is proposed. The existing API contract supplies matching
cell count/type, and cache slots already carry exact original bytes and a typed deleter.
Aggregate accounting cannot diagnose every wrong-but-balancing caller count, double
free, foreign pointer or type mismatch. Caller admission remains a source/audited-probe
obligation; underflow invalidates the report when detected. No global stored heap
pointers are introduced, avoiding a diagnostic registry that could hide leaks from
ASan/LSan. The ledger cannot list which allocation occupied the peak; that is later
identity/event instrumentation with separate overhead and sanitizer treatment.

Coverage records state **C++ blade_pool API element capacity only**, with explicit
unknown-release detection at the two known null-extents bypasses. No whole-scalar-heap,
whole-runtime, pointer-skeleton, provider raw-buffer, BLAS/workspace or LLVM coverage.

## 3. Measurement instant and coherence

One process-wide allocation-free short lock protects **all** fields in one POD ledger.
A compile-time initialized atomic_flag is sufficient for the pilot. Every capacity
transition, checked counter update, peak update, sequence/witness update and snapshot
copy is one transaction. Independent per-field atomics are insufficient and are rejected.

These are **API-linearized accounted-capacity snapshots and peaks**. A fresh success
publishes capacity after native new[] succeeds and before alloc returns. A direct delete
or eviction removes capacity after native delete[] completes and before the wrapper
returns. Cache transfers linearize with cache slot mutation. Peak combined is the maximum
of A+R in this serialized event history, not a sum of separate component peaks.

Never hold the ledger lock over native new[]/delete[], constructors/destructors,
new_handler, TLS/Reaper registration, getenv, report serialization, file I/O or a callback.
Those operations may reenter allocation or synchronize with another thread; holding a
global allocator lock across them could deadlock or change failure behavior.

Consequently native allocation/deletion can be **in flight** outside a ledger transaction.
A native allocation may have succeeded before its success event acquires the lock; a
native delete may already have finished before its removal event. Concurrent event order
can differ from physical heap overlap, so even a coherent API peak is **not an exact
instantaneous native-live allocation high water**. Physical-live peaks remain unmeasured.
Do not retrofit a physical interpretation by timing events or summing independent peaks.

Expose `api_operations_in_flight`, `native_new_in_flight`, `native_delete_in_flight` in
snapshots, all checked under the same lock. A snapshot does not wait for quiescence
(waiting inside a reentrant callback could deadlock); `quiescent` means all are zero.
Independent expected **current** capacity assertions use explicit caller barriers and
quiescent snapshots. Peak assertions refer only to the stated API-linearized history.
No report claims that past native overlap is exact merely because its final snapshot
is quiescent. This limitation is part of the schema/metric names, not a footnote.

Maintain `peak_active`, `peak_retained`, `peak_combined` independently. Retain the first
combined-peak witness `(event_seq, active_bytes, retained_bytes)`; its two components sum
to that peak. Example: states (A,R)=(B,0),(0,B),(B,B),(2B,0),(0,2B) have component peaks
2B and 2B but simultaneous combined peak **2B**, not 4B.

## 4. Event state machine

`B` is checked `cells*sizeof(T)` for a fresh call or direct release; eviction uses its
existing slot's recorded byte count. Blocks and all byte/event counters update in the
same transaction. Native operations remain outside that transaction.

| event / linearization point | active delta | retained delta | other obligations |
|---|---:|---:|---|
| native new[] attempt begins | 0 | 0 | attempts +1, native-new in-flight +1; no successful bytes |
| native new[] succeeds, before alloc returns | +B | 0 | native-new in-flight -1; successful allocations +1 and cumulative **successful native element bytes** +B; public allocation success recorded once |
| native attempt throws bad_alloc / other exception | 0 | 0 | native-new in-flight -1; correct failed-attempt class +1; preserve original exception and rethrow/retry behavior |
| cache hit, with removal of exact type/count slot | +B | -B | one public allocation success/cache hit; no native allocation or cumulative native bytes |
| release retained, with insertion into releasing thread's cache | -B | +B | release/retention transfer +1; same combined capacity; Reaper registration happens before this short transaction |
| release direct typed delete[] completes | -B | 0 | native-delete in-flight balanced; successful native deletion +1; no fake retention |
| retained typed eviction/delete[] completes | 0 | -B | one eviction and native deletion; cache-byte/slot mutation reconciled with this event |
| null release | 0 | 0 | no block/bytes invented; preserve existing null-delete behavior |
| native direct delete throws (permitted throwing element destructor) | unknown | unknown | invalidate completeness and all numeric/in-flight/quiescence availability as unknown native deletion outcome, rethrow the original exception; never guess whether native storage was freed |
| flush / thread exit | only its actual deletions | only its actual deletions | no reset, no blanket subtraction, no fake active release |
| known unknown-count direct release | unknown | unknown | increment unknown-release evidence and permanently invalidate numeric completeness; no guessed count |

Native begin/end events can nest through legitimate native callback behavior; bookkeeping
itself calls no allocator and never recursively acquires the lock. The ledger adds no
new retry, catches-and-swallows no application exception and changes no typed deletion.
Existing cache mutation/legacy stats semantics remain intact. Any cache-slot mutation
grouped with ledger work contains fixed scalar assignments/shifts only, not native calls.

**OOM retry:** preserve the existing eligible-miss path exactly. First native attempt
failure contributes zero successful capacity. Flush only the current cache, recording
its actual evictions. Record one retry before the second attempt. Second success creates
one active block; second failure creates none and propagates the original exception.
An ineligible/cache-off miss keeps its existing no-flush/no-retry behavior. Constructor
exceptions that are not caught by the current bad_alloc path still propagate unchanged.
Legacy `fresh` need not equal the new successful-native count, especially on failures.

## 5. Checked arithmetic and report validity

Use unsigned integer capacities/event counts with explicit representability checks for
cells*sizeof(T), A/R/combined additions, all subtractions, block counts, cumulative bytes,
sequence, attempts and in-flight counters. Compute a proposed next POD state, validate
every operation/invariant, then commit it as a unit; never publish half a transfer.
No wrapping, silent saturation, floating-point totals or negative capacity.

Validity is independent of coverage and endpoint quiescence. An unknown-count release,
underflow, arithmetic overflow or violated accounting precondition sets a **sticky**
`completeness=invalid` with first event sequence/reason. Continue the original allocation,
cache and exception behavior; diagnostics must not throw bad_alloc, free a returned
pointer, retry extra work, clamp an application size or terminate a valid program.

After invalidation, numeric current/peak/cumulative capacity fields are null/unmeasured
for the whole reported run, with an optional explicitly labelled valid-prefix record.
Do not keep printing partial numbers as complete final peaks. Error flags/evidence can
use fixed flags and a bounded POD reason enum; no dynamic strings inside the lock.
An unrepresentable request may subsequently throw natively; do not replace that native
exception just because the byte model cannot represent it.

The existing cache budget sum `c.bytes+b` is safe under today's default/approved retain
policies, but arbitrary compile-time policy constants could overflow. The pilot must
either prove `kBudget <= SIZE_MAX-kMaxBlock` at diagnostic admission or declare the policy
unsupported/incomplete. It must not silently change cache-fit policy under this card.
A separate reviewed subtraction-form cache-fit repair is needed if such policy constants
must be supported. No diagnostic byte multiplication relies on eligibility alone.

## 6. Threads, exit and disabled routes

The ledger is one process-wide inline, constant-initialized, trivially destructible POD
state plus lock. It remains usable through TLS destruction; it owns no heap and has no
destructor/report-order dependency. No per-origin-thread active totals are decremented
merely because that thread exits. A pointer may legally outlive its allocating thread
and be released into another thread's cache.

Thread-exit Reaper flush removes only that thread's retained entries. Later destruction
on the same thread observes `tl_cache.dead` and accounts the existing direct-delete
route, never a renewed retention. Cross-thread release and reuse update the shared A/R
transaction; no TLS enumeration or forced flush of another thread's cache is added.

Pilot endpoints: explicit named snapshots; worker threads joined where closure is
asserted; main-thread flush explicitly requested by the probe. No reset API and no
automatic process-exit "all zero" summary. A zero-leaf higher-rank sentinel remains
active at the final snapshot; this is valid coverage of a real outstanding pool, not
an instrumentation failure or permission to fix the leak in this card.

Build opt-in proposal: `BLADE_POOL_CAPACITY_DIAGNOSTICS=1`, default absent/off. All state,
includes, hooks, lock traffic and reporting API are compiled out when disabled: **zero
diagnostic work**, with existing Stats behavior untouched. Initial use is the standalone
probe only; no new compiler flag or cached runtime environment gate. Runtime activation
and ordinary generated-program reporting are later work if needed.

With cache-off (including the header's ASan auto-disable), every intercepted success is
active, matching direct delete removes it, and retained capacity/peak remain zero. Keep
native typed new/delete pairing and sanitizer-visible frees. The diagnostic owns no
pointer registry or retained copies of application addresses. Diagnostic data/static
overhead is separately identified, never claimed as included tracked allocation bytes.

## 7. Proposed interface/output and next approval boundary

Tentative `blade_pool::capacity_snapshot_v1()` returns a fixed POD copy with schema,
scope/measurement-kind IDs, validity/reason/sequence, quiescence/in-flight counts,
current capacities/block counts, three independent peaks, combined-peak witness and
attempt/success/failure/cache-transfer/eviction/flush/retry counters. Names spell out
`api_accounted_element_capacity`; no generic `live_bytes`/`allocation_peak` field.

The standalone probe writes a JSON sidecar outside allocator/lock code, plus independent
assertion outcomes. If diagnostic stderr is later added, use a distinct tagged record;
never contaminate timing/checksum stdout or mutate the legacy Stats record. Caller
serialization memory and instrumentation static storage are outside tracked capacity.

Implemented hooks, **not yet executed or accepted on validation evidence**: fresh success/failure and native attempt
boundaries in both alloc branches; cache-hit and retain transfers; post-native eviction
and direct-release completion in both release branches; explicit unknown-release hooks
at both null-extents teardown bypasses. Keep APIs/type tags/cache policy/typed deleters
otherwise unchanged. The gated helper is `src/cpp/blade_pool_capacity.hpp`, not a new
allocator. No LLVM/provider/compiler/F# changes. Blade.fsproj's header deployment list
remains unchanged; the new opt-in helper is source-tree/standalone-only in this pilot.
Normal generated programs preprocess its include out. A future generated-program
diagnostic integration must separately package the header and approve driver/reporting.

Root approved the bounded API-linearized metric/interface, physical-overlap exclusion,
unknown-release invalidation, no-registry caller assumption, sentinel treatment and
[probe acceptance matrix](../../diagnostics/pool-capacity/spec.md). Actual implementation
still requires root diff review before sole-runner validation. Exact physical-native live
peaks or allocation identities require another design; this pilot does not supply them.
