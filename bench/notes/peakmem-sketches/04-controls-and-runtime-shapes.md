# Memory controls and runtime-shaped storage

Sketches only, 2026-10-21. No compilation, emission, execution or benchmarks.
These C++20-shaped blocks describe generated code/measurement intent. Helper names are
proposed contracts, not existing runtime APIs. See [shared runtime sketches](00-region-runtime.md).
All dimensions, offsets, byte counts and reservations use checked arithmetic. Controls
compare identical outputs and lifetimes; they are not benchmark results.

## PM49 Copy, scale, add and triad

Target: emitted scalar/pointwise loops and harness traffic labels. Inputs are immutable,
distinct arrays; all output cells must be stored and observed after the interval.
No custom vector intrinsic or non-temporal-store policy is implied.

```cpp
// Four separate fixtures, with the same N and output-lifetime rule.
for (uint64_t i = 0; i < N; ++i) copy_out[i] = a[i];
for (uint64_t i = 0; i < N; ++i) scale_out[i] = alpha * a[i];
for (uint64_t i = 0; i < N; ++i) add_out[i] = a[i] + b[i];
for (uint64_t i = 0; i < N; ++i) triad_out[i] = a[i] + (alpha * b[i]);
```

These are separate arms, not four simultaneous required outputs unless that larger task
is declared. Float64 useful bytes are 16N for copy/scale and 24N for add/triad. Hardware
traffic may include allocation/write-ownership reads, eviction and cache reuse. `triad`
uses the existing contraction policy; this sketch does not force FMA. Odd extents and
fixed output validation prevent alignment/cache artifacts or dead-store elimination from
masquerading as bandwidth gains.

## PM50 Strided traversal

Target: characterize physical layout cost while retaining a dense N-cell output.

```cpp
require(stride >= 1);
uint64_t storage_count = N == 0 ? 0
    : checked_add(checked_mul(N - 1, stride), 1);
require(input.capacity() >= storage_count);
for (uint64_t i = 0; i < N; ++i)
    out[i] = input[checked_mul(i, stride)];
```

The input footprint grows with stride and is reported. Sweeps such as 1/3/7/17 and odd
neighbors of page/cache boundaries distinguish stride from total-footprint changes.
Useful bytes remain 2bN for this copy task; touched cache lines may be far larger. An
indexed-output or terminal-reduction variant is a different fixture, not a substitute
that quietly avoids the required stores.

## PM51 Gather locality

Target: compare identical gathers with different declared index distributions; within an
A/B pair the index sequence and result are identical. This is a stored-output control.

```cpp
// Keys have already been validated in the original program's required order.
// Otherwise retain ordered bounds checks at the original access positions.
for (uint64_t i = 0; i < M; ++i)
    out[i] = source[key[i]];
```

Use permutations, clustered runs and repeated keys. No sorting of keys is allowed in the
storage-preserving arm: that changes result order unless restored and can move failures.
Report key bytes, source footprint, result bM, and modeled cache-line amplification.
Prefetch must be bounded and cannot dereference an unvalidated key early. PM28 owns the
separate gather-to-terminal elimination experiment.

## PM52 Store then consume

Target: distinguish reduced input traffic from eliminating an array that must remain.
The fixture requires both the output and a scalar folded in ascending order.

```cpp
// Baseline: required array, then consumer.
for (uint64_t i = 0; i < N; ++i) y[i] = producer(x[i]);
double a = seed;
for (uint64_t i = 0; i < N; ++i) a = step(a, y[i]);
retain_until_observation(y, a);

// Candidate: SAME required stores; producer and consumer/seed admitted pure/total.
double b = seed;
for (uint64_t i = 0; i < N; ++i) {
    double v = producer(x[i]);
    y[i] = v;
    b = step(b, v);
}
retain_until_observation(y, b);
```

The two blocks are alternative programs, not sequential writes to one shared fixture.
The candidate removes a reread, not bN mandatory storage. Seed timing, intervening effects,
producer totality and consumer totality must all justify interleaving. A delayed consumer
control retains the delay's real work and live roots. Non-temporal stores need their own
policy arm and may hurt an immediate consumer; no such intrinsic is added here.

## PM53 Worker bandwidth saturation

Target: assess worker count alongside workspace. The source already licenses the required
parallelism; codegen owns scheduling. Independent rows below do not combine partial sums.

```cpp
auto credits = TRY(ctx.reserve_bundle(
    checked_worker_bundle(P, per_worker_scratch))); // payloads + owner-table metadata
auto all_worker_storage = TRY(ctx.allocate_worker_buffers_with_credit(
    std::move(credits), P, per_worker_scratch));     // allocate, do not reserve again
schedule_disjoint_rows(P, rows, [&](WorkerId w, RowRange owned) {
    auto scratch = all_worker_storage.take_owner(w); // distinct backing plus its credit
    for (RowId r : owned) {
        double acc = original_seed_for_row(r);
        for (uint64_t c = 0; c < width(r); ++c)
            acc = original_step(acc, input(r, c), scratch.span());
        output[r] = acc;
    }
    worker_cache(w).accept_or_free(std::move(scratch)); // keep credit if backing retained
});
join_all_workers();
all_worker_storage.release_empty_owner_table(); // no blanket release of payload credits
```

The scheduling helper invokes exactly one callback per worker ID, with its complete
disjoint row range; it guarantees one writer per output and no shared mutable scratch.
Initial kernels/seeds are total and immutable; unsupported effects refuse.
The allocation helper divides the credit bundle among real buffers and owner metadata,
and rolls back failures before starting workers. Existing retained blocks remain charged
in the same ledger; do not reserve their cache-capacity allowance a second time. Moving
active backing into a cache moves its existing credit; direct free releases credit only
after deallocation. Nested teams share the ledger, and it outlives worker-cache teardown.
Include required input/output storage. P=1/2/4/8 is an experiment, not a promised policy.

## PM54 Page touch and allocation

Target: explain requested/reserved/touched bytes and working-set/commit differences.

```cpp
Result<void> dense_output_fixture(ExecutionContext& ctx, uint64_t N) {
    auto out = TRY(ctx.allocate_uninitialized<double>(N));
    for (uint64_t i = 0; i < N; ++i) out[i] = value_for(i);
    observe_full_output(out); // every required cell assigned
    return success();
}

Result<void> sparse_touch_fixture(ExecutionContext& ctx, uint64_t diagnostic_bytes) {
    auto pages = TRY(ctx.allocate_uninitialized_bytes(diagnostic_bytes));
    for (uint64_t off = 0; off < diagnostic_bytes; ) {
        pages[off] = byte{1};
        uint64_t step = min(page_stride, diagnostic_bytes - off); // page_stride > 0
        off += step;
    }
    observe_probe_addresses(pages); // only initialized probe addresses
    return success();
}
```

The harness selects one fixture per fresh child process; it never runs these bodies
back-to-back under one process high-water measurement. The diagnostic probe therefore
does not retain a dense result, allocator history or an earlier dense working-set peak.
Never score the sparse probe as a completed dense kernel or read uninitialized bytes.
Page size/stride comes from the platform/fixture, not a guessed universal constant.
Report the probe as diagnostic and separate allocator-retained memory from live output.
Physical-page residency can change without changing either the allocation or its capacity.

## PM60 Runtime-shaped reuse

Target: extend `Optimize.planPoolReuse` beyond literal shapes while keeping axis identity
distinct from capacity. First admit identical runtime shapes of the same plain scalar
element representation; capacity-compatible different shapes are a later branch.

```cpp
// All original shape/value checks happen at their original observable positions.
// Donor's last consumer has completed. No view/capture/caller alias can observe it.
Array fresh;
if (proof.root_is_dead_and_private(donor)
    && proof.same_element_representation(donor, requested)
    && equal_runtime_extents(donor.shape, requested.shape)
    && proof.compatible_alignment_and_layout(donor, requested)) {
    // Transfer ownership/reservation; do not free, copy, or charge the pool twice.
    fresh = adopt_backing(donor.detach_owner(), requested.logical_index_identity);
} else {
    fresh = TRY(allocate_for(requested));
}
fill_every_required_cell(fresh); // or perform the required initialization first
```

The proof calls denote optimizer facts, not runtime reflection. Runtime extent comparison
only selects among already legal alternatives. Equal byte counts do not prove equal rank,
named index identity, element lifetime, strides or row skeleton layout. Do not retag the
source value: the new wrapper carries the destination's type identity.

For later capacity reuse, compare checked requested capacity with donor capacity, then
construct/reuse a compatible metadata skeleton under the same reservation ledger. Charge
old-plus-new metadata while both exist. Initial support should stay with trivial scalar
elements; generic object lifetime/destructor reuse is not implied. Clear the old frame's
ownership record so scope exit cannot free the transferred allocation again. A complete-
write proof may omit initialization; sparse updates and early exits cannot.

Second-look questions to answer by reading, then eventually by implementation evidence:
does any required output disappear; does a stride/index overflow precede pointer creation;
does a transferred pool have exactly one owner; do retained caches stay charged; are
diagnostic touch probes clearly distinct from completed workloads? No answers in this
file are presented as measured results.
