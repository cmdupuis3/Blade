# B03: instrumented scalar-pool accounting pilot

Scoped only; not assigned or implemented. Predecessor B02 is accepted.

The source census finds a bounded typed cache in
`src/cpp/nested_array_utilities.hpp`, namespace `blade_pool`. Its existing
`BLADE_POOL_CACHE_STATS` counters count eligible fresh/reused/retained/evicted
events, not live or retained byte capacity. `alloc<T>`, `release<T>`, cache hit,
`evict_oldest` and `flush` expose the transitions for one honest accounting
pilot. The graft query did not resolve these C++ symbols; targeted source
inspection supplied this census.

Proposed single card: an opt-in diagnostic ledger for allocation paths passing
through this C++ scalar-pool allocator, with a standalone deterministic probe.
Do not change retention policy, add last-use release, or claim coverage of LLVM,
pointer skeletons, providers' raw chunk buffers, library workspaces or the heap.
Keep existing statistics compatibility and zero diagnostic work when disabled.

Before coding, specify the event state machine and measurement instant:
successful fresh allocation creates active capacity; cache reuse transfers
retained to active capacity; release transfers active to retained or returns it
to the system; eviction removes retained capacity. Count exact allocated cells,
including the existing empty-array sentinel when supplied by allocate<>.
Distinguish active payload capacity, retained capacity and their simultaneous
sum; separate peaks cannot be added to manufacture a simultaneous peak.
Do not call active capacity logical liveness, RSS, commit, or DRAM traffic.

Resolve accounting synchronization and exception behavior in the handoff.
Independent atomics do not by themselves give a coherent simultaneous peak.
Do not count failed allocations as successful capacity or double-count an OOM
retry after flushing. Preserve typed deletion, cross-thread release behavior,
cache-disabled/ASan routes and thread-exit flush. Arithmetic overflow must be
handled explicitly. Diagnostic code must not recurse through the allocator.

A bounded test should exercise small ineligible allocations, eligible odd-size
allocation, exact-size reuse, different element types, retained eviction,
explicit flush, empty sentinel and cache-off. Independently expected event and
capacity totals must validate a snapshot, not merely parse its own report.
Add multithread/cross-thread cases if the ledger claims process-wide coverage.
The sole Luna runner builds and executes it; no benchmark or full suite here.

Root reviews the state machine before a production edit. If safe, narrowly
scoped accounting cannot be established, close this card with the evidenced
limitation and a precise next design requirement rather than misleading bytes.
