# Region planning and bounded runtime: implementation sketches

Sketches only, 2026-10-21, source baseline `667250cd`. Nothing in this directory is built,
compiled, emitted, executed or benchmarked. F# and C++ below are illustrative shapes,
not patches or existing APIs. Unimplemented helper contracts are stated explicitly.
These sketches implement the ideas in the [streaming plan](../peakmem-streaming-optimizer-plan.md);
they do not add source-language loop syntax or a public stream type.

## R01 Normalized region facts and discovery

Target: `Optimize.optimizeModule` and the two analysis seams in `Lowering.fs:3131–3185`.
The following records are a proposed private analysis vocabulary, not additions already
present in `IR.fs`. Collect them while high-level producer structure still exists, then
rebuild them against final IDs after lifting/forcing/CSE.

```fsharp
type MaterializationOrigin = ExplicitSnapshot | InsertedByLowering | Deferred
type Demand = ScalarTerminal | ResidentValue | PrintedSnapshot | ExternalSink
            | Escapes | ExtentOrIndex | UnknownUse
type Recipe = Stored of IRId
            | Provider of IRId
            | ScalarMap of callable: IRId * input: Recipe
type FoldLeg = {
    Kernel: IRId
    Seed: IRExpr
    ValueRecipe: Recipe
    ResultType: IRType          // each leg has its own accumulator type
    Licence: FoldLicence       // preserve; don't take another leg's licence
}
type Candidate = {
    Recipe: Recipe
    Legs: FoldLeg list
    Members: Set<IRId>
    Origin: MaterializationOrigin
    Demands: Set<Demand>
    Roots: Set<StorageRoot>
}
```

`FoldLicence` and `StorageRoot` are placeholders for facts derived from existing IR/types;
they are not assumed existing types. `Members` includes hidden payloads and type/extent
uses, not just syntactic children. Use the `bindingPayload`, callback, embedded-extent,
compaction-root and foreign-reference machinery described in the plan. A named source
variable is not proof that its underlying allocation is private.

Do not retain executable decisions from discovery. Store recipes, origin and source spans;
at finalization resolve live IDs, recalculate roots/versions, and reject a recipe whose
source changed or whose demand is no longer closed.

## R02 Narrow terminal admission before broad region ownership

The first pilot is a single immutable input followed by scalar maps and scalar terminals.
This pseudocode spells out rejection order rather than hiding legality in `isSafe`.

```fsharp
let admitPilot facts candidate =
    let refuse why = Error (candidate.Members, why)
    if candidate.Demands <> Set.singleton ScalarTerminal then
        refuse "a resident, printed, escaped, indexed or unknown use survives"
    elif candidate.Origin = ExplicitSnapshot then
        refuse "pilot does not remove explicit snapshot materialization"
    elif not (facts.HasCompletePayloadAndCallbackCensus candidate) then
        refuse "incomplete demand/effect census"
    elif not (facts.OneImmutableInputVersion candidate.Recipe) then
        refuse "multiple, mutable or unresolved source versions"
    elif not (facts.AllMovedMapsFoldsSeedsArePureTotal candidate) then
        refuse "pilot has no ordered-failure proof alternative"
    elif not (facts.SourceValidationsRetainOrder candidate) then
        refuse "provider validation or seed timing would change"
    elif not (facts.LogicalCoordinatesAndFoldOrderAgree candidate) then
        refuse "axis, seed, traversal or arithmetic graph mismatch"
    elif not (facts.DisjointFromLegacyPlannerCandidates candidate) then
        refuse "S1 pilot overlaps an existing storage/schedule planner"
    elif not (facts.BackendHasExactRoute candidate) then
        refuse "unsupported backend/shape/producer chain"
    else
        match deriveCheckedSynchronousSchedule facts candidate with
        | Error why -> refuse why
        | Ok schedule -> Ok (candidate, schedule)
```

The fact methods above are proposed proof obligations: purity/totality can reuse
`IRTotality` for supported scalar bodies; no-write alone cannot implement the test.
Provider reads are not total. They must follow the required validation order, and the
pilot does not publish any result until the read finishes. Seeds are literal/immutable
total expressions in the initial examples; no seed may be moved across a possible
failure merely because the consuming arithmetic is total.

S1 uses conservative disjointness. If that leaves only cases already fused, record the
no-gain result and defer a new optimization until coordination exists. Do not make an
unprofitable pilot look useful by stripping required output demand.

## R03 Ownership and all-or-nothing emission

For broader S3 regions, a compilation-local ownership registry arbitrates with
`planPoolReuse`, `planHotRowFolds`, `planCompactionFusion` and `planCompactRowClasses`.

```fsharp
type RegionOwnership = {
    Generation: int
    NodeOwner: Map<IRId, RegionId>
    RootOwner: Map<StorageRoot, RegionId>
}

let canLegacyPlanTouch ownership candidate =
    // Include donor + recipient + aliased backing + deferred/side-table operands.
    Set.isEmpty (Set.intersect candidate.AllNodeIds ownership.ClaimedNodeIds)
    && Set.isEmpty (Set.intersect candidate.AllRoots ownership.ClaimedRootIds)

let chooseEmission backend budget ir candidate =
    match prepareCompleteRegionEmission backend ir candidate with
    | Ok prepared when prepared.Generation = ir.Generation ->
        // Preparation creates text/IR fragments but mutates no emitted program.
        if budget.Allows prepared.Bound then Region prepared
        else Refuse "declared resource limit cannot admit this region"
    | _ ->
        match prepareOriginalEmission backend ir candidate with
        | Ok fallback when budget.Allows fallback.Bound -> Original fallback
        | _ -> Refuse "no supported bounded fallback"
```

`prepareCompleteRegionEmission` must accept every producer and consumer before skipping
any original binding. It cannot emit half a region, discover an unsupported provider,
then append a materialized fallback with conflicting names/ownership. Reset ownership
with existing per-compilation tables. Plan reports include both the chosen route and
the specific failure reason for discarded alternatives.

## R04 One execution-wide capacity ledger

Target: proposed runtime helper beside the allocation/runtime headers, not a changed
allocator policy. This is initially synchronous; a concurrent version needs one locked
or otherwise proven atomic reservation operation for the entire bundle.

```cpp
struct Reservation {             // context outlives every token
    ExecutionContext* owner;
    uint64_t bytes;
    Reservation(const Reservation&) = delete;
    Reservation& operator=(const Reservation&) = delete;
    // Consuming moves clear the source. Construction/move/release are noexcept.
    // Destructor returns remaining credit; storage owners destroy backing first.
    // No allocation, registration table, or growing token-ID registry.
};

Result<Reservation> reserve_bundle(ExecutionContext& e,
                                   uint64_t requested) {
    // e.reserved includes all live reservations, caches and cleanup capacity.
    if (requested > e.limit - e.reserved) // invariant: reserved <= limit
        return resource_limit(requested, e.limit, e.reserved);
    e.reserved += requested;
    e.high_water = max(e.high_water, e.reserved);
    return Reservation::take_noalloc(e, requested);
}

Result<Buffer> acquire_buffer(ExecutionContext& e, Shape s, ElementLayout t) {
    uint64_t capacity = checked_storage_capacity(s, t); // payload + skeleton
    Reservation r = TRY(reserve_bundle(e, capacity));
    auto allocation = try_allocate(capacity, t.alignment);
    if (!allocation) return allocation_failure(); // r returns unused credit
    return Buffer{allocation, move(r), s, t};
}

Buffer::~Buffer() noexcept {
    destroy_elements_and_free_backing(allocation);
    reservation.release_noexcept(); // never make credit available before free
}
```

Every `TRY`, `Result`, `Buffer` and `checked_*` name is a helper placeholder. `TRY` here
means propagate through the execution context's cleanup path, not call `std::exit`
without cleanup. The subtraction test requires the stated invariant. Overflow checks
precede reservation, allocation and pointer formation. Byte widths come from element
and metadata layouts, not an assumption that all values are doubles.

An allocation retained in a cache keeps its reservation. A borrowed view has no second
payload charge but keeps its root live. Moving a reservation between owners does not
charge twice. Allocator overhead and codec internals outside coverage must be reported;
this ledger is not an automatic process-RSS cap. `Buffer` is also noncopyable and has
consuming moves; borrowed descriptors keep its owner alive. Constructing its owner must
be nonthrowing after allocation, or a temporary allocation guard must free on failure.

For a chosen synchronous schedule, enumerate allocation/release transitions on paper:

```text
bound = max over steps of capacity(live roots UNION next-step roots UNION cleanup roots)
reserve every root needed to complete the next step before starting that step
reject below this sufficient bound; do not wait while holding the blocks needed to progress
```

This is a schedule-specific upper bound. Dynamic cardinalities need certified upper
bounds or checked reservations and a preselected bounded fallback. Merely summing
logical array lengths is neither an allocation trace nor a progress proof.

## R05 Borrowed windows, aligned streams and backpressure

```cpp
template<class T> struct BorrowedWindow {
    uint64_t logical_origin;
    uint64_t valid_count;        // <= backing capacity
    Generation generation;
    const T* data;               // points into the actual allocation
    BorrowToken token;           // prohibits recycle while a borrower exists
    const T& at(uint64_t logical_i) const {
        require_current_generation(token, generation);
        require(logical_i >= logical_origin);
        uint64_t local = logical_i - logical_origin;
        require(local < valid_count);
        return data[local];
    }
};
```

Generated validated loop accesses may eliminate these checks after proving the exact
index range. Never form `data - logical_origin`, never allocate an N-row skeleton to
expose a B-row block, and never return this descriptor as an ordinary escaping array.

For two zip inputs, preserve coordinate identity and advance at the next common logical
boundary; do not choose the least common multiple of their physical chunk sizes:

```text
require same named axis and extent; prove source validation order can be preserved
for cursor from 0 to N, using arithmetic cursors:
    obtain/retain source windows covering cursor under the total byte budget
    next = next compute-block boundary strictly greater than cursor (checked arithmetic)
    end = min(windowA.end, windowB.end, next, N)
    require end > cursor; zero-length/noncovering windows are a reader contract error
    consume the range [cursor,end) in its logical order
    release only windows whose last borrower has finished
    cursor = end
```

Multi-provider interleaving can change which source failure occurs first. S2 therefore
uses one source; S3 needs a specific provider-order admission or remains declined.
Physical decode granularity can exceed compute-block size and counts at its full size.

The initial shared-fanout route runs each total consumer on the same block synchronously
and releases it only when all are finished. A later asynchronous queue reserves bytes
for compressed, decoded, queued and reorder-buffer states before submission. A task-count
limit alone is not a byte bound. A branch waiting for a global statistic cannot retain
an unbounded list of borrowed blocks; it selects replay/spool/materialize before starting.

## R06 ST02: connect a block reader to map recipes and joined folds

Existing `CodeGenBinding.fs:3807` supplies a direct streamed scalar fold. Joined reductions
use a separate fiber route that rejects rank-1 elementwise consumption. The new route
would generate an arithmetic block loop directly; it must not reuse the ordinary streamed
map wrapper that allocates a full result and full run table.

Concrete Float64/Int64 example, with a required map graph `y = (x*scale)+bias`:

```cpp
// PRE: static/checked N, B>0, immutable source, Float64 maps are pure/total.
// PRE: seed timing is legal; count_seed + N fits Int64 for this fixture.
// PRE: reader's prescribed validations run in source order; no early publication.
auto storage = TRY(ctx.acquire_region_buffers(
    checked_mul(B, sizeof(double)), provider.max_decode_workspace(),
    constant_descriptor_and_fold_bytes));
double* input = storage.input;
double sum = sum_seed;           // independent source seed, evaluated once
double sumsq = sumsq_seed;
int64_t count = count_seed;      // not coerced into a Float64 tuple
auto validation = provider.begin_original_read_validation(snapshot);

for (uint64_t lo = 0; lo < N; ) {
    uint64_t len = min(B, N - lo);
    TRY(provider.read_block_into(snapshot, lo, len, input, storage.decoder, validation));
    for (uint64_t q = 0; q < len; ++q) {
        double y = (input[q] * scale) + bias;
        double yy = y * y;
        sum = sum + y;
        sumsq = sumsq + yy;
        count = count + int64_t{1};
    }
    lo += len;                  // <= N, no lo+B overflow
}
TRY(provider.finish_original_read_validation(validation));
publish_result(sum, sumsq, count); // after all source validation succeeds
```

The fixture's nonzero seeds are part of the reference; never replace them with zero or
merge block sums. Strict mode retains these independent chains; contraction policy is
still the compiler's existing policy. No `fma` intrinsic forces contraction in an off
build. Licensed variants need a separately stated legal tree and their own sketch later.
This code promises a storage shape, not vectorization or a measured speedup.

`validation` carries the original whole-read diagnostics, including missing-cell count
and earliest coordinate where required. Missing-value conversion cannot throw at the
first block if the source operation accumulates diagnostics until assembly finishes.
Later read failures retain their original precedence over that final diagnostic. The
block route must either preserve this behavior or decline; delaying publication alone
is not a sufficient proof. See `ProviderRegistry.fs:204–215` for the current convention.

Future verification (not performed): route marker in emission plus runtime route evidence,
no N-sized intermediate, fixed-B metadata/decoder scaling, odd tails, differing seed types,
pure/total refusal negatives, and comparison with materialized `.read` results. Output
equality alone can pass when the route silently falls back.

## R07 Checked sequential spool across a statistic barrier

The S4 pilot stores exact fixed-width values, one writer followed by one reader. Its
format has bounded in-memory metadata: a fixed header, bounded block header, payload,
integrity tag, and final count/footer. Regular block offsets are arithmetic; no vector
of one index record per input element or chunk is retained in RAM.

```cpp
template<class T> Result<void> CheckedSpool<T>::append(Span<const T> values) {
    require(state == Writing && 0 < values.size() && values.size() <= block_capacity);
    uint64_t next_count = checked_add(logical_count, values.size());
    uint64_t following_ordinal = checked_add(next_ordinal, uint64_t{1});
    uint64_t payload = checked_mul(values.size(), encoded_width<T>());
    uint64_t growth = checked_add(block_header_and_tag_bytes, payload);
    TRY(owner.disk.grow_owned_credit(credits, growth));
    // Keep credit charged even after a partial write, until owned file is removed.
    auto bytes = exact_bit_encoding(values, bounded_encoding_buffer);
    auto header = encode_header(next_ordinal, values.size());
    TRY(write_exact(file, header));
    TRY(write_exact(file, bytes));
    TRY(write_exact(file, integrity_tag(header, bytes)));
    logical_count = next_count;  // commit counters only after the complete write
    next_ordinal = following_ordinal;
    return ok();
}

Result<void> CheckedSpool<T>::seal(uint64_t expected_count) {
    require(state == Writing && logical_count == expected_count);
    TRY(write_reserved_footer_and_check_close());
    state = Sealed;
    return ok();
}
```

Any append/seal failure makes this spool unusable except for cleanup; no retry can append
after a partial record. The one spool-owned credit counter grows without a per-block
registration or heap object:

```cpp
Result<void> DiskLedger::grow_owned_credit(DiskCredit& c, uint64_t n) {
    require(c.owner == this);
    uint64_t next_owned = checked_add(c.bytes, n);
    // One locked transaction if concurrency is introduced; no fallible operation below.
    if (n > limit - reserved) return disk_quota_exceeded();
    reserved += n;
    c.bytes = next_owned;
    high_water = max(high_water, reserved);
    return ok();
}
```

Header/footer space and all simultaneous spool generations are reserved too. Reservations enforce
the configured quota; they do not guarantee the filesystem can satisfy a write. Short
writes, disk full, close failures, truncation and integrity failure are explicit errors.
Exact encoding preserves floating payloads/signed zeros; it is not a numeric conversion.

```cpp
// All of these capacities are charged, including their simultaneous overlap.
auto work = TRY(ctx.acquire_spool_workspaces(
    provider.max_input_capacity(), provider.max_decode_workspace(),
    checked_mul(B, sizeof(double)), checked_mul(B, encoded_width<double>()),
    checked_mul(B, encoded_width<double>()), fixed_reader_writer_metadata));
auto staged = work.staging_span<double>(); // exactly B cells, B > 0
auto spool = TRY(ctx.create_private_spool<double>(
    shape, B, snapshot_identity, work.encoding_buffer));
auto validation = provider.begin_original_read_validation(snapshot_identity);
double total = seed;
TRY(for_each_input_block_in_original_order(work, validation,
  [&](auto input) -> Result<void> {
    // A physical provider block can be larger than B; never write past staging.
    for (uint64_t lo = 0; lo < input.size(); ) {
        uint64_t len = min(B, input.size() - lo);
        for (uint64_t q = 0; q < len; ++q) {
            staged[q] = producer(input[lo + q]);
            total = total + staged[q];
        }
        TRY(spool.append(staged.first(len)));
        lo += len;
    }
    return ok();
}));
TRY(provider.finish_original_read_validation(validation));
TRY(spool.seal(N));
double mean = total / prescribed_divisor;  // exact written arithmetic graph
double answer = answer_seed;
auto reader = TRY(spool.reader(work.read_buffer, staged)); // reuse staging for decode
while (auto block = TRY(reader.next_checked_block())) {
    for (double y : *block)
        answer = answer + transform(y, mean); // pure/total, same original order
    block.release();             // no borrowed block survives the next refill
}
TRY(reader.finish_and_check_footer());
TRY(reader.close_checked());     // close OS handle before removal, including Windows
TRY(spool.remove_owned());
publish_result(answer);
```

The reader is a bounded state machine, not a deserializer allowed to size allocations
from untrusted record lengths:

```text
open: check fixed header, schema, element encoding, logical shape and snapshot identity
next block:
    read one fixed-size record tag/header exactly; EOF here is truncation
    if footer tag: save the bounded footer and return end-of-blocks
    require block ordinal == expected ordinal and 0 < count <= B
    checked next total = total + count; require next total <= declared N
    checked payload bytes = count * encoded width; require <= reserved read capacity
    read exact payload and integrity tag; verify header + payload before yielding
    advance expected ordinal and total; yield borrowed exact-bit decoded values
finish:
    require footer, original N == total == footer count, and matching block count
    check final integrity metadata and no trailing bytes; report checked close errors
```

Fixed-width decoding either borrows suitably aligned native bits or uses the B-cell
staging buffer after writing has ended. A distinct decoded buffer, if required, must be
charged separately; the reader cannot secretly allocate it.
Empty inputs contain no data blocks and still require a valid header/footer. Error cleanup
closes handles before removal; failed removal retains ownership metadata and disk credit.

The callback/result/optional syntax is deliberately schematic; `TRY` must propagate to
one cleanup path across callback boundaries. The original problem requests only the
terminal. A resident normalized array would add its mandatory allocation and stores.
The map/statistic and second transform use S4's total-only admission. An online normalizer
is a separate algorithm and is not implemented by substituting a different update here.

Spool I/O introduces new policy-visible resource failures. Preserve the original program's
semantic validation/diagnostic order when resources succeed; do not claim that disk-full
has an equivalent failure in a non-spooling baseline. No public output is exposed early.

## R08 Cleanup, recovery and sinks

```text
execution start:
    create private run directory under configured scratch root
    acquire exclusive ownership lock; record nonce + pid + process-start identity
    create execution-scoped memory/disk ledgers and pre-reserved cleanup state
normal completion / handled failure / cancellation:
    stop submissions -> join/cancel outstanding work -> close owned handles
    remove owned scratch -> release quota only for successfully removed files
    retain manifest of cleanup failures; finish reporting -> release ownership lock
recovery after abort/immediate exit/forced kill/host loss:
    inspect only this facility's manifests
    acquire exclusive ownership and prove recorded owner dead (pid alone insufficient)
    if uncertain, retain; otherwise remove only verified owned paths
```

Normal-exit handlers and, where used, quick-exit handlers supplement explicit cleanup.
They cannot guarantee cleanup after all termination modes. Do not use an age-only purge.
Validate resolved paths under the configured root, ownership and links before recovery;
a manifest is not authority to delete arbitrary paths. This is a proposed lifecycle,
not a promise that current runtime error paths already implement it.

A public chunk sink adds `begin_private`, `stage`, `validate_complete`, `commit`, `abort`.
Only a provider with a specified commit operation can expose the new result. A directory
rename is not assumed to implement every provider's transaction or overwrite semantics.
All output cells and validations required by the source still occur. If the task returns
an ordinary resident array too, keep that array; a sink does not erase its ABI demand.

```text
Private/Staging -> Ready: all required production and semantic validation succeeded
failure before commit: close and abort private data, then surface the selected error
Ready -> Committing -> ConfirmedCommitted: transfer ownership; never destructive-abort
Ready -> Committing -> ConfirmedNotCommitted: abort private data and report failure
Ready -> Committing -> OutcomeUnknown: retain reconciliation metadata and owned staging;
    provider-specific recovery establishes publication before cleanup is allowed
```

A cleanup failure does not erase the original semantic/resource error or count as a
successful deletion. Record remaining owned paths and charges. Uncertain publication is
neither success nor an ordinary abort; a provider lacking this recovery contract declines
the proposed sink route. Private staging must accept exact invalid floating bit patterns
until the original writer's complete nonfinite tally can be reported after production.
