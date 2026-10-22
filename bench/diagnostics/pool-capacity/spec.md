# B03 standalone scalar-pool capacity probe specification

**Implementation authored after root design approval; unexecuted, pending diff review.**
2026-10-22. Parent [B03 design](../../notes/round4/B03-design.md) defines API-linearized
element-capacity accounting, validity and exclusions. `probe.cpp` and the gated header
hooks implement this contract; root reviews the actual diff before Luna alone compiles
or runs it. [B03 handoff](../../notes/round4/B03-handoff.md) supplies frozen identities
and exact runner requests. The coder has run no validation.

## Work and independent observation contract

Use a small standalone C++ probe including the exact source-tree runtime header, with
explicit `BLADE_POOL_CAPACITY_DIAGNOSTICS=1`. Run each scenario in a **fresh process**;
do not reset counters over live/retained pools. Policy is default unless a row explicitly
names cache-off or ASan. No timing benchmark, Blade compiler build or full suite.

Portable fixed examples require `sizeof(double)==8`, `sizeof(int64_t)==8`; otherwise
report unsupported rather than retain hardcoded expected totals. Extents are small and
odd. Source-independent assertion constants/formulas below must not be imported from
the report being tested. Validate native pointer reuse/type behavior and values as well
as the snapshot. Snapshot parsing alone is not acceptance.

Let **B = 2053*8 = 16,424** bytes (eligible under default policy),
**D = 2059*8 = 16,472** (different count), and
**H = 393217*8 = 3,145,736** (eligible, just above 3 MiB). `A,R,C` denote the current
active, retained and combined bytes; `C=A+R` at every valid snapshot. `PA,PR,PC` are
separate API-accounted peaks, never PA+PR. Check block counts alongside bytes.

The probe observes/assigns valid cells before release, then never dereferences a
released/cached pointer. Pointer equality for exact-size reuse compares captured address
values without reading freed memory. Native type/deletion checks may reuse the existing
allocation-layout test convention, but must not introduce a pointer-root registry that
masks the sanitizer leak report.

## Deterministic serial matrix (fresh child per row)

| scenario | operations and required quiescent assertions |
|---|---|
| S1 small and rank-one empty sentinel | Allocate 3 doubles (A=24), then allocate<double*> with extents[0]=0 (actual one cell, A=32). Release first (A=8), deallocate empty with known extents (A=0). Final R=C=0; PA=PC=32, PR=0; 2 native successes, 32 cumulative successful native element bytes, 2 direct deletions, 0 retains. Zero logical extent must not be counted as zero capacity. |
| S2 exact reuse and same-byte different type | Allocate double[2053], release => (0,B). Allocate same type/count => same pointer and (B,0), one cache hit and no new native bytes. Allocate int64_t[2053] => native miss, distinct live pointer, (2B,0). Release double then int => (0,2B); flush => (0,0). Public alloc successes=3; native successes=2; native successful bytes=2B=32,848; retains=3; eviction deletes=2. PA=PR=PC=2B (not PA+PR=4B). |
| S3 different count | Allocate/release double[2053] => R=B. Allocate double[2059] => A=D,R=B,C=B+D=32,896; no hit. Release => R=B+D; flush => 0. Native successes=2, successful bytes=32,896, retains=2, evictions=2; PA=D, PR=PC=B+D. |
| S4 slot eviction | Allocate 17 simultaneous double[2053] pools => A=17B=279,208. Release the first 16 => A=B,R=16B=262,784. Releasing the seventeenth evicts the oldest, then retains incoming => A=0,R=16B. Final flush deletes 16 more. Native successes=17; retains=17; evictions before explicit flush=1, after flush=17. PA=PC=279,208; PR=262,784. Record transient eviction state before the last transfer, not one fabricated net event. |
| S5 byte-budget eviction | Allocate 3 simultaneous double[393217] => A=3H=9,437,208. First two releases retain 2H=6,291,472; third evicts one oldest block because 3H exceeds default 8,388,608 budget, then retains incoming. Before flush A=0,R=2H; after flush 0. PA=PC=3H; PR=2H; native successes=3, retains=3, evictions=1 then 3 after flush. No retention-policy change. |
| S6 above cap and non-arithmetic direct path | Allocate/release an odd extent above default 4 MiB cap and a small std::complex<double> pool. Both successes count actual cells*sizeof(T), neither retains or hits. Check expected object-size capacity independently; no claim constructors' external allocations are included. Typed delete and returned values must remain correct. |
| S7 direct zero-cell API | Direct alloc<double>(0), then matching release count 0. Successful/active block count becomes 1 then 0; element bytes stay zero. Do not invent malloc cookie/usable bytes. Keep separate from allocate's one-cell sentinel. |
| S8 higher-rank zero-leaf sentinel | allocate<double**> with extents {0,3}, then normal known-extents deallocate. Header's documented unrecoverable sentinel remains outstanding: final A=C=8, active_blocks=1,R=0,PA=PC=8. Flush must leave it 8. No forged zero and no leak repair in this pilot. This expected outstanding allocation is a standalone non-sanitizer child, not a passing leak-free sanitizer assertion. |

Every snapshot also asserts complete validity, scope/measurement-kind IDs, quiescence,
block/byte invariants, combined-peak witness sum and monotonic event sequence. Legacy
Stats output/counters remain their existing meaning; do not force `fresh==success` in a
failure scenario.

## Failure and invalidity matrix

Inject native failure only in the probe's **native allocation test seam/global operator
new[] replacement**, never by adding a production allocation-failure policy. A fixed
one-shot arm, typed sizes and call counters distinguish the requested native call from
unrelated runtime allocation. If deterministic injection cannot isolate it without
affecting the program/ledger, that cell remains blocked pending a reviewed test seam.

| scenario | expected result |
|---|---|
| F1 OOM then success | First retain a B-byte double block. Request a different eligible D-byte double block. Fail the first native attempt; existing path flushes B then retries once and succeeds. A=D,R=0; peak combined=max(B,D)=D; attempts=3 total (including preload), bad_alloc failures=1, native successes=2, successful native bytes=B+D; retry=1; OOM flush=1, eviction=1. Failed attempt contributes **zero** successful capacity. Release/flush closes the successful result. |
| F2 OOM twice | Same preload. Fail both request attempts. Catch the original bad_alloc at caller; retained preload was flushed. A=R=C=0, PA=PR=PC=B, attempts=3, failures=2, native successes=1, successful native bytes=B, one retry. No capacity or allocation-success event for either failure. |
| F3 ineligible/cache-off failure | A forced small ineligible or cache-off native failure propagates with no added retry/flush and no successful capacity. Existing legacy fresh eligibility behavior remains unchanged. |
| F4 constructor exception | A small non-arithmetic test element throws from construction. Native array cleanup/exception remains unchanged; no successful block bytes counted. Nested allocations, if deliberately routed through blade_pool, are separate counted calls. Do not charge partially constructed array capacity as a returned successful allocation. |
| F5 unknown-count bypass | Allocate a known rank-one pool, then deallocate with extents=nullptr. The new explicit unknown-release hook records missing size and invalidates completeness; final numeric capacities/peaks are null. Native direct delete behavior continues. No guessed size and no apparently complete positive remainder. Repeat for allocate_strict/deallocate_strict bypass. |
| F6 arithmetic/precondition invalidation | Exercise pure bookkeeping transitions at representability boundaries via a diagnostic-only test interface; no enormous real allocation. Overflow/underflow/sequence/in-flight overflow produce sticky invalidity, no partial transfer, wrapping or saturation. An invalid-policy byte-bound refusal is explicit. No synthetic state injection API in ordinary builds. |

Invalid snapshots distinguish **scope coverage** from **invalid completeness**. After
invalidation, no final numeric capacity/peak is advertised as exact. A valid-prefix
record is explicitly truncated and not a whole-run high water. Probe stdout and JSON
must distinguish failed application allocation from failed/incomplete accounting.

## Process-wide concurrency and exit matrix

Use barriers/latches plus joined threads to make independently expected states, not
sleeps, races or scheduler-dependent peaks. All IDs/native callbacks stay outside the
short ledger lock. Never add a reset/forced cross-thread cache flush to ease assertions.

| scenario | required evidence |
|---|---|
| T1 concurrent active/retained states | Two workers each hold a B-byte pool, barrier => A=2B,R=0. Command one release while the other remains active, barrier => A=B,R=B. Release second, barrier => A=0,R=2B. Workers exit/join => A=R=0. PA=PR=PC=2B; witness satisfies A+R=2B. Independent peaks must not fabricate PC=4B. |
| T2 origin exits with active pool | Worker allocates B-byte pool, publishes pointer through synchronized stack state, exits without releasing it. Main after join sees A=B,R=0; origin exit must not subtract it. Main releases => R=B in **main's** cache; main flush => 0. |
| T3 cross-thread retained ownership | One thread allocates; another releases and pauses before exit. Joined/synchronized snapshot sees R=B process-wide. Releasing thread exits and its Reaper flushes => 0. Allocating thread exit cannot double-subtract the block. |
| T4 late TLS destructor | Construct a late-release TLS object before first retention; it owns one active B-byte pointer. Retain a second B-byte pool, registering Reaper later. At exit Reaper runs first, flushes retained pool and marks cache dead; late object then releases active pointer via direct delete. After join A=R=0; native successes=2, retains=1, eviction deletes=1, direct delete=1. This checks post-Reaper dead-cache behavior. |
| T5 reentrant native allocation callback | A controlled new_handler/native test seam requests an accounting snapshot and, if safe, a small independent pool call while an outer native allocation is in flight. No ledger lock is held over the native callback; no deadlock and snapshot says non-quiescent. Preserve original retry/exception behavior. Do not claim arbitrary reentrant mutation of the same native cache is made safe by diagnostics. |
| T6 synchronized live snapshots and optional concurrent stress | Two workers each perform 64 allocate/write/release operations in four segments of 16. Two-phase barriers require the observer to sample while both workers hold their 1st, 17th, 33rd and 49th allocations: exactly four required observations, each A=2B and R=0, before either worker can release those blocks or complete its next segment. Between checkpoints a bounded observer loop samples while workers may continue; its separately printed optional stress count may be zero. Every sampled copy must be valid, obey C=A+R and the witness invariant, and have nondecreasing sequence/combined peak. The checkpoint samples prove non-vacuous live-work endpoints, not simultaneous native mutation; concurrent stress/interleaving remains scheduling-dependent. No exact **physical** capacity assertion is made during native overlap. Independent joined totals require 128 successes, two native successes, 126 cache hits, two evictions, PC=2B and A=R=0. |

## Cache-off, sanitizers and disabled instrumentation

Compile a cache-off probe with `BLADE_POOL_CACHE_OFF=1` and run bounded S1/S2/T2 variants.
Retention/hits/retained peak must be zero; native successes include every API allocation
(S2's three calls become three native successes/cumulative 3B). Direct releases close
all released pools; no false reset at thread exit.

ASan variant must preserve the header's automatic cache disable, native typed frees and
sanitizer diagnostics. Do not run the intentionally leaked S8 as a leak-free positive
test. A separate expected-leak negative can establish that enabling diagnostics has not
rooted application pointers and hidden a known unreleased pool. Keep genuine leak or
double-free behavior outside production ledger repair; if required sanitizer support is
missing, record the skip explicitly.

Compile the same small semantic probe with capacity diagnostics disabled, with legacy
Stats alone, and with both enabled. Check equal values/typed allocation outcomes and
unchanged legacy record shape/meaning. Inspect disabled output/artifact to establish no
capacity state, lock or event work remains; do not use timing noise as proof of zero
diagnostic work. Existing optional retention policies require their own independently
derived expectations; no default-policy verdict is extrapolated to them.

## Implemented interface and scenario map

`BLADE_POOL_CAPACITY_DIAGNOSTICS=1` includes the allocation-free ledger/helper; absent or
zero compiles its state/includes/hooks/work out. `capacity_snapshot_v1()` returns an
optional numeric POD: after sticky invalidity the optional is absent, so its JSON
`api_accounted_element_capacity` **and quiescence are null**, never synthetic zeros.
Numeric/in-flight bookkeeping stops after invalidation; it is no longer available for
any final-current or whole-run-peak interpretation. Scope and first invalid reason /
last valid event sequence remain visible. There is no production reset or registry.

Probe argv is `<scenario> <ABSOLUTE-report.json>`. Exit 0 means its independent assertions
passed (including expected invalidity or intentionally outstanding sentinel in those
negative scenarios); exit 1 means assertion/report failure; exit 2 means bad arguments,
unsupported scenario/build combination or report-open failure. Each scenario is one
fresh process. stdout records individual assertions and their total, stderr retains
legacy Stats when compiled. JSON gives coverage, measurement kind, compiler/policy,
ledger static-storage overhead and named snapshot evidence. Complete provenance includes
the runner's source/header/executable hashes and exact command lines, not just JSON text.

| spec cells | implemented scenarios |
|---|---|
| S1–S5 | `small`, `reuse`, `different-count`, `slots`, `budget` |
| S6/S7/S8 | `direct`, `zero`, `sentinel`, `strict-sentinel` (S8 for both allocator forms) |
| F1/F2/F3 | `oom1`, `oom2`, `fail-small`, `fail-eligible-off` |
| F4 | `ctor`; `dtor` additionally preserves throwing-destructor exceptions and invalidates unknown native deletion completion |
| F5 and null negative | `unknown`, `unknown-strict`, `null` (null storage does not invalidate) |
| F6 | `overflow`, `underflow`, `sequence-overflow`, `inflight-overflow`, `counter-overflow`, `product-overflow`, `unsupported-policy` |
| T1–T6 | `threads`, `origin-exit`, `retainer-exit`, `late`, `reentry`, `observer` |
| cache-off/ASan/disabled/provenance | `cache-off`, `small`, `origin-exit`, `null`, `fail-eligible-off`; `semantic` across defines; optional expected-leak `leak` |

Failure/invalidity/reentrance seams exist only under the additional
`BLADE_POOL_CAPACITY_TESTING=1` and diagnostic guard. Native allocation injection is a
size-selected callback immediately before the real new[] expression, outside the ledger
lock. No ordinary allocation failure behavior/policy is changed. Overflow helpers only
invalidate a fresh test process; they do not expose state reset. A production-opt-in
build without TESTING must also be compiled and exercised for ordinary witnesses.

The B03 helper is not added to Blade.fsproj's explicit deployment list in this standalone
pilot. Default generated programs do not include it after preprocessing; enabling it for
deployed/generated programs is a separate packaging/driver card. Do not silently use a
stale deployed runtime for this source-tree probe.

Exact isolated runner requests/hashes live in [B03-handoff.md](../../notes/round4/B03-handoff.md).
No hidden full suite, benchmark or performance run belongs to this probe. Report exact
pass/fail/skip totals, policy, cache/sanitizer mode, source/compiled identity and native-vs-API
measurement coverage. Unknown-release/overflow/in-flight/unsupported metrics remain explicit.
