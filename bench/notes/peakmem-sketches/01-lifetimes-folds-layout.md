# Lifetime, fold and layout implementation sketches

Status: SKETCH ONLY, 2026-10-21. Source baseline `667250cd`. **Uncompiled and
unexecuted.** These are candidate generated C++20/runtime fragments, not Blade
source, patches to the compiler, existing APIs, or performance claims. Generated
C++ loops implement declarative Blade operations; no imperative Blade surface is
proposed. The active speed-plan owners retain their work.

Scope: exactly PM01–PM16 and PM25–PM32 in the
[parent plan](../peakmem-microkernel-plan.md), incorporating the
[parent review](../peakmem-microkernel-review-claude.md) and
[streaming review](../peakmem-streaming-review-claude.md).

Common conventions: each fragment is a separate emitted function body. `T` is
the source's concrete scalar width, initially Float32/Float64 unless stated;
`size_t` indexes storage and is never substituted for a signed language integer.
All products and offset sums are checked before allocation at their original
validation sites. Strict examples assume repro and contraction-off; no loop
implies a reassociation licence. The compiler must retain source seeds and
per-cell parenthesization, not reconstruct them from a real-number formula.

Helpers below are **proposed contracts**, not imported Blade APIs:

- `owned_cells<T>(n)` checks `n*sizeof(T)`, allocates a move-only contiguous
  owner using uninitialized arithmetic storage, and exposes `operator[]`,
  `data()` and `span()`. Zero cells never cause a read. Ownership transfer
  transfers the eventual typed deleter; `.reset()` releases through the chosen
  allocator policy. Headers, extents and skeletons are accounted separately.
- `SourceSite` identifies the original diagnostic location;
  `empty_reduce(site)` raises the existing BL8003 there. `source_*` callbacks
  below mean the exact already-lowered expression/kernel, with concrete types,
  captured versions and diagnostic sites, not arbitrary user callbacks.
- `ordered_coordinates(shape)` enumerates the exact logical coordinate order
  of the specified operation, including each independent reduction chain.
  A shape with zero volume yields no coordinates. It is not a reordered tile walk.
- `region_total` means a compiler certificate covering all moved producer,
  consumer, seed and intervening payloads, including callbacks and side tables.
  The initial schedules require purity and totality; an ordered-failure analysis
  is deferred. Reading a provider is not automatically pure or replayable.

The shared ordered state has no implicit zero identity or block combination:

```cpp
template<class T, class Step> struct Ordered {
    std::optional<T> acc;  // source seed, or nullopt for an unseeded fold
    Step step;
    void push(T x) {
        if (acc) *acc = step(*acc, x); // exact source argument order
        else acc = x;                 // first element is the unseeded value
    }
    T finish(SourceSite site) const {
        if (!acc) empty_reduce(site);
        return *acc;
    }
};
```

Every proposed schedule remains subject to the final demand census: print,
returned arrays/tuples, captured views, provider writes, type/index metadata and
deferred uses. A checksum cannot remove a required result. Allocation-resource
failures have the separate resource-policy contract; these fragments do not
claim unchanged allocator timing or whole-process RSS.

## PM01 Barrier chain

Target `Optimize.planPoolReuse` (`src/Optimize.fs:548`). This rank-1 example has
three maps separated by required scalar observations. The first array is dead
before its third-stage overwrite; neither scalar retains its storage.

```cpp
auto a = owned_cells<T>(n);
for (size_t i = 0; i < n; ++i) a[i] = source_stage0(input[i]);
const T s0 = source_observe0(a.span()); // whole-array barrier, kept
auto b = owned_cells<T>(n);
for (size_t i = 0; i < n; ++i) b[i] = source_stage1(a[i], s0);
const T s1 = source_observe1(b.span()); // whole-array barrier, kept
// No remaining observer of a; this stage reads b, never a.
for (size_t i = 0; i < n; ++i) a[i] = source_stage2(b[i], s1);
b.reset();
return std::tuple{std::move(a), s0, s1};
```

Target two physical `n*sizeof(T)` buffers rather than three scope-long buffers.
Keep every source operation in its existing stage. A view/capture of old `a`, a
return alias from either observer, or an indirect read of `a` in stage 2 refuses
donation. The existing same-shape route may already emit this schedule: census first.

## PM02 Early release with escaped views

Target the scope frees at `src/CodeGenLoopNest.fs:4863–4905`. The planner's unit is
a backing root, not a wrapper name. A shared owner here illustrates lifetime;
proven static lifetimes should use direct scheduled frees without refcount overhead.

```cpp
template<class T> struct Slice {
    std::shared_ptr<T[]> root;
    T* data;
    size_t length;
};
auto root = std::shared_ptr<T[]>(new T[n], std::default_delete<T[]>{});
source_fill(root.get(), n);
Slice<T> row{root, root.get() + lo, hi - lo}; // bounds proved before this
const T statistic = source_observe(root.get(), n);
root.reset();                    // row still owns the backing root
auto result = source_consume(row.data, row.length);
// Compiler facts cover returned views, tuple members and deferred/captured results.
attach_result_lifetime_roots(result, consumer_result_root_facts, row.root);
row.root.reset();                // frees only if result retained no root token
return std::tuple{std::move(result), statistic};
```

`attach_result_lifetime_roots` is a proposed compile-time-directed ownership operation:
for a fresh/scalar result it does nothing; a returned view/closure receives the root
token in its existing internal ownership representation before `row` releases it.
Unknown result escapes decline early release. Include tuple, side-table and deferred
borrowers; borrowed extent metadata lives as long as its data views. Only the last
actual borrower frees the root, rather than every wrapper reset or frame exit.

## PM03 Last-use pointwise update

Extend reuse beyond a donor that is dead before the consumer reads it. Initially
admit a sole-owned fresh dense buffer and a pure total same-cell map only.

```cpp
// owner is moved into this function; no caller, view or capture retains it.
T* p = owner.data();             // one legitimate read/write pointer path
for (size_t i = 0; i < n; ++i) {
    const T old = p[i];
    const T next = source_pointwise(old, immutable_coefficients);
    p[i] = next;
}
return std::move(owner);
```

Remove the additional `n*sizeof(T)` destination. **No restrict/noalias annotation
is implied.** Re-prove emitter dependence and SIMD/thread metadata separately:
the current flat path assumes fresh output (`CodeGenLoopNest.fs:2783–2830`).
Shifted reads, stencils, unproven overlapping views, mutation, failure and donor
observability refuse this first route. Scalar locals preserve source DAG reuse.

## PM04 Sibling materialized outputs

Two pure total maps share input loads but both arrays remain required outputs.
This is a speed-owner extension/control, not permission to discard either output.

```cpp
auto y = owned_cells<T>(n);
auto z = owned_cells<T>(n);
for (size_t i = 0; i < n; ++i) {
    const T x = input[i];
    const T yi = source_map_y(x);
    const T zi = source_map_z(x);
    y[i] = yi;                    // required stores
    z[i] = zi;
}
return std::tuple{std::move(y), std::move(z)};
```

Target one input traversal; required payload stays `2*n*sizeof(T)`. FIR variants
share the exact halo loads and retain each filter's tap chain. Interleaving the
maps changes evaluation order, so a later map failure or callback effect refuses
the first version. Output overlap or an input mutation between old stages also refuses.

## PM05 Forked producer DAG

A private producer feeds two terminal folds. A block retains each produced value
until both consumers finish it; each fold keeps its own ordered chain across blocks.

```cpp
auto block = owned_cells<T>(block_capacity); // capacity > 0
Ordered<T, StepA> a{seed_a, source_step_a};
Ordered<T, StepB> b{seed_b, source_step_b};
for (size_t lo = 0; lo < n; ) {
    const size_t count = std::min(block_capacity, n - lo);
    for (size_t k = 0; k < count; ++k)
        block[k] = source_producer(lo + k);
    for (size_t k = 0; k < count; ++k) a.push(source_a(block[k]));
    for (size_t k = 0; k < count; ++k) b.push(source_b(block[k]));
    lo += count;
}
const T av = a.finish(site_a); // retain original terminal order
const T bv = b.finish(site_b);
return std::pair{av, bv};
```

Target `block_capacity*sizeof(T)` instead of a full private producer. Initial
admission proves all moved expressions pure/total and seeds dominant/stable.
For cheap producers, a separately costed replay route runs `source_producer(i)`
again for the second consumer. Global-summary dependencies or lagging consumers
cannot retain an unbounded block queue; choose materialize, replay or explicit spool.

## PM06 Per-worker workspace

Parallel independent rows reuse one scratch owner per active worker. `run_workers`
is a proposed executor: invoke exactly `workers` callbacks with distinct IDs, join
them before return, and charge their reservations to one execution context.

```cpp
auto credits = TRY(ctx.reserve_bundle(
    checked_worker_bundle<T>(workers, scratch_cells))); // payloads + owner-table metadata
auto storage = TRY(ctx.allocate_worker_cells_with_credit<T>(
    std::move(credits), workers, scratch_cells));       // no second reservation
run_workers(workers, [&](size_t tid) {
    auto scratch = storage.take_owner(tid); // unique buffer and its reservation credit
    // partition_rows supplies disjoint contiguous ranges covering [0, rows).
    const auto [lo, hi] = partition_rows(rows, tid, workers);
    for (size_t r = lo; r < hi; ++r) {
        source_fill_scratch(r, scratch.span());
        result[r] = source_row_result(r, scratch.span());
    }
    worker_cache(tid).accept_or_free(std::move(scratch)); // credit follows retained memory
});
storage.release_empty_owner_table(); // table credit only; never release cached payload credit
```

`allocate_worker_cells_with_credit` partitions the bundle into one credit per actual
buffer plus descriptor credit; it rolls back incomplete allocation before launch.
Cache acceptance moves the existing typed owner and credit, without reserving again;
rejection frees backing before releasing that credit. Existing retained cache blocks
are already charged, not added again as an unused cache-cap allowance. Cache reuse
would likewise transfer its existing credit into the active buffer. The execution
context outlives worker-cache teardown. Require disjoint writes, a source parallel
licence and total callbacks; nested teams share one budget. Each row must fit scratch,
and no result/capture may retain a scratch view into the next row or cache lifetime.

## PM07 Complete-write initialization

Existing recurrence zero-fill elision is the control (`TypeCheckInfer.fs:14057`;
`CodeGenExpr.fs:2075`). Generalize only with a complete-write-before-read proof.

```cpp
static_assert(std::is_arithmetic_v<T>);
check_allocation_count<T>(n);   // checked n*sizeof(T), original shape guards kept
auto output = std::make_unique_for_overwrite<T[]>(n);
for (size_t i = 0; i < n; ++i) {
    const T value = source_cell(i);
    output[i] = value;          // every observable cell assigned exactly once
}
return output;
```

Remove an otherwise redundant initialization write, not the output itself.
The proof must cover early exits, every conditional arm, empty extents and all
future reads. Sparse updates and recurrence zero-history reads keep needed zeros.
Do not apply arithmetic uninitialized-storage reasoning to class elements whose
construction has semantics; in particular complex storage needs its own route.

## PM08 Allocation churn and retention

Sketch the existing exact-size policy's mechanism, plus a separately selected
capacity policy. One cache is shared across element types **per thread**;
`type_token<T>()` is a unique address per concrete allocation type.

```cpp
// Proposed move-only CachedBlock/TrackedCells carry pointer, typed deleter and credit.
// Their no-throw moves empty the source; reset frees backing before releasing credit.
struct ProposedCache {
    static constexpr size_t slot_capacity = 16;
    std::array<std::optional<CachedBlock>, slot_capacity> slots;
    size_t count = 0, held = 0;
    ExecutionContext& ctx;
    const size_t byte_budget, slot_limit; // constructor validates slot_limit <= 16
    ProposedCache(ExecutionContext&, size_t byte_limit, size_t max_slots);
    ProposedCache(const ProposedCache&) = delete;
    ProposedCache& operator=(const ProposedCache&) = delete;
    ProposedCache(ProposedCache&&) = delete; // one stable instance per thread
    ProposedCache& operator=(ProposedCache&&) = delete;
    CachedBlock take(size_t k) noexcept {
        auto b = std::move(*slots[k]);
        for (size_t j = k + 1; j < count; ++j) slots[j - 1] = std::move(slots[j]);
        slots[--count].reset();
        held -= b.bytes;
        return b; // credit remains live, now owned by the returned block
    }
    void evict_oldest() noexcept { auto b = take(0); b.reset(); }
    template<class T> TrackedCells<T> acquire(size_t n) {
        for (size_t k = count; k > 0; --k) {
            const auto& b = *slots[k - 1];
            if (b.type == type_token<T>() && b.cells == n) {
                return TrackedCells<T>::adopt(take(k - 1)); // no additional charge
            }
        }
        return allocate_tracked_cells<T>(ctx, n); // checked capacity, allocation + credit
    }
    template<class T> void release(TrackedCells<T> owner) noexcept {
        auto b = owner.detach_block(); // allocation-time checked metadata + reservation
        if (!eligible_arithmetic_block<T>(b.cells) || b.bytes > byte_budget ||
            slot_limit == 0) return;    // b destroys backing, then releases its credit
        while (count != 0 && (count >= slot_limit || held > byte_budget - b.bytes))
            evict_oldest();
        const size_t bytes = b.bytes;
        slots[count++].emplace(std::move(b)); // fixed slot + no-throw move: no allocation
        held += bytes;
    }
    ~ProposedCache() { while (count != 0) evict_oldest(); }
};
```

The control's eligibility is arithmetic blocks of 16 KiB–4 MiB, 8 MiB retained
and 16 slots per thread (`nested_array_utilities.hpp:99–104`); a large-pool arm
changes the declared policy rather than claiming existing support. Counters track
active and retained bytes separately. The fixed slot table is charged once as metadata;
release cannot allocate bookkeeping storage or lose ownership on insertion failure.
`CachedBlock`/`TrackedCells` are proposed ownership helpers, not existing classes. Their
destruction/credit release must be no-throw. The context outlives caches; releases after
thread-cache teardown need a direct-free route, and sanitizer bypass remains explicit.

## PM09 Deferred full fold

Use `IRReduceCompute` and inspect existing fusion before adding a path. Rank-1
deforestation feeds the original fold directly; no chunk partials are introduced.

```cpp
Ordered<T, Step> total{source_seed, source_step};
for (size_t i = 0; i < n; ++i) total.push(source_map(i));
return total.finish(fold_site);
```

Higher-rank full reduction must follow its lowered tree, not flatten by habit.
For the explicitly specified row-then-outer seeded tree, the emitted shape is:

```cpp
Ordered<T, OuterStep> total{outer_seed, source_outer_step};
for (size_t r = 0; r < rows; ++r) {
    Ordered<T, InnerStep> row{source_row_seed(r), source_inner_step};
    for (size_t c = 0; c < cols; ++c) row.push(source_cell(r, c));
    total.push(row.finish(inner_site));
}
return total.finish(outer_site);
```

The seed callbacks above reproduce the actual IR's evaluations; this is not a
claim that every `axes=k` spelling uses these seeds. Target no producer pool,
only nested scalar states. Initially require pure total moved kernels and
appropriate nonempty facts; retain the unseeded empty error at its original site.

## PM10 Deferred partial fold

`R` is the product of unreduced extents, `C` the reduced innermost extent. The
result retains all unreduced index identities; rank 3 with `axes=1` stays rank 2.

```cpp
auto result = owned_cells<T>(R);
for (size_t r = 0; r < R; ++r) {
    // decode_outer preserves the original outer-axis coordinate order.
    const auto coord = decode_outer(r, unreduced_shape);
    Ordered<T, Step> row{source_seed_for(coord), source_step};
    for (size_t c = 0; c < C; ++c)
        row.push(source_producer(coord, c));
    result[r] = row.finish(source_row_site(coord));
}
return result;
```

Target required `R*sizeof(T)` output plus scalar state rather than `R*C` cells.
The initial route requires pure total producer/fold/seed and known legal extents.
A later producer failure versus an earlier row-fold failure is a mandatory
refusal example. Do not merge row states, drop seeds, or assume ragged/compact
axes have the plain dense traversal. Source seam: `Optimize.planHotRowFolds:2579`.

## PM11 Multiple statistics

Share one load while retaining separate source graphs and seeds. The `min_step`
below is the exact source min kernel, including its signed-zero/NaN behavior;
substituting `std::min` by convenience is not authorized.

```cpp
Ordered<T, Add> sum{sum_seed, source_add};
Ordered<T, Add> sq{square_sum_seed, source_add};
Ordered<T, Min> minimum{min_seed, source_min_step};
std::int64_t count = count_seed; // overflow excluded by an admission proof
for (size_t i = 0; i < n; ++i) {
    const T x = input[i];
    sum.push(x);
    const T product = x * x;
    sq.push(product);
    minimum.push(x);
    count = count + std::int64_t{1};
}
const T s = sum.finish(sum_site);
const T q = sq.finish(square_site);
const T m = minimum.finish(min_site);
return std::tuple{s, q, m, count};
```

Target one traversal and O(number of statistics) state. Empty-result finishing
must occur in original source order; seeds must dominate with immutable captures.
Each licensed leg keeps its own licence; one fast leg cannot authorize the others.
Current sibling-fold sharing is `Optimize.fs:4928`; active speed ownership stays.

## PM12 Row normalization

Concrete workload: row mean, centered sum of squares, then `(x-mean)/sqrt(var)`.
This keeps the exact three-pass arithmetic graph while removing centered and
squared arrays. Other normalizers require their own literal lowered graph.

```cpp
auto out = owned_cells<T>(checked_product(rows, cols));
for (size_t r = 0; r < rows; ++r) {
    T sum = source_sum_seed;
    for (size_t c = 0; c < cols; ++c) sum = sum + input(r, c);
    const T mean = sum / source_float_extent(cols);
    T ss = source_square_seed;
    for (size_t c = 0; c < cols; ++c) {
        const T centered = input(r, c) - mean;
        const T square = centered * centered;
        ss = ss + square;
    }
    const T variance = ss / source_float_extent(cols);
    const T scale = source_sqrt(variance);
    for (size_t c = 0; c < cols; ++c)
        out[r * cols + c] = (input(r, c) - mean) / scale;
}
return out;
```

Initial rows/operations are pure total and nonempty with admissible scale;
unknown zero-variance/domain cases retain original scheduling/checks. Required
output is `rows*cols*sizeof(T)`, workspace O(1). Do not replace this by Welford,
`sum(x*x)-mean*mean`, a reciprocal multiply, or centered-value reconstruction.

## PM13 Pairwise weighted normalization

Explicit two-pass workload per query: `z=sum(weight)`, `u=sum(weight*value)`,
then `u/z`. Recompute the same pure total weight graph to avoid a pair matrix;
online softmax remains a separately specified algorithm.

```cpp
auto out = owned_cells<T>(queries);
for (size_t q = 0; q < queries; ++q) {
    T z = z_seed;
    for (size_t j = 0; j < samples; ++j) {
        const T w = source_weight(q, j);
        z = z + w;
    }
    T u = u_seed;
    for (size_t j = 0; j < samples; ++j) {
        const T w = source_weight(q, j);
        const T term = w * values[j];
        u = u + term;
    }
    out[q] = source_final_divide(u, z);
}
return out;
```

Keep default finite/nonfinite semantics of the chosen source graph, including
all-masked behavior; do not insert a stabilizing max unless it was written.
Target O(queries) mandatory output and O(1) scalar workspace. A row buffer is a
valid competing plan for expensive weights. Unstable captures/external reads,
failing replay or a demanded weight matrix block this schedule.

## PM14 Array-valued accumulator

Two separate arrays support a fold kernel whose next cell may read arbitrary
cells of the previous state. Each full next state commits before the next step.

```cpp
auto prev = owned_cells<T>(width);
auto next = owned_cells<T>(width);
for (size_t c = 0; c < width; ++c) prev[c] = source_seed[c];
for (size_t k = 0; k < steps; ++k) {
    for (size_t c = 0; c < width; ++c)
        next[c] = source_next_cell(prev.span(), inputs[k], c);
    std::swap(prev, next); // atomically advances the logical whole-state value
}
next.reset();
return prev;
```

Target two `width*sizeof(T)` pools rather than one fresh array per iteration.
The proposed callback is exactly the total pure state kernel, writes every
next cell, and neither stores nor returns a previous-state alias. Earlier states
must be unobserved. Same-cell specializations may use PM03 after an independent
proof; arbitrary-state reads never overwrite `prev` during the step.

## PM15 Existing ragged segment reductions

Offsets and source rows already exist. Validate them at their original sites;
this sketch never constructs a padded `groups*max_length` array.

```cpp
auto out = owned_cells<T>(groups);
for (size_t g = 0; g < groups; ++g) {
    const size_t lo = offsets[g], hi = offsets[g + 1];
    Ordered<T, Step> fold{source_group_seed(g), source_step};
    for (size_t p = lo; p < hi; ++p)
        fold.push(source_group_value(g, p - lo));
    out[g] = fold.finish(source_group_site(g));
}
return out;
```

`source_group_value` reads the existing group's encounter-ordered row (or the
matching validated contiguous segment); it does not sort members. Output is G
scalars with original group identity. Empty seeded groups retain their seed;
unseeded groups fail at the original group fold. Streaming a whole arbitrary row
kernel may still require max-row workspace: this scalar fold does not justify
a constant-memory claim for every grouped operation.

## PM16 Sorted projection

Start with pure total stable keys having a pinned ordering contract: integers
or proven ordered finite floating keys. Unspecified NaN ordering is outside the
route. Sort once, then gather each required projected column from the same permutation.

```cpp
auto perm = owned_cells<size_t>(n);
for (size_t i = 0; i < n; ++i) perm[i] = i;
std::stable_sort(perm.data(), perm.data() + n, [&](size_t a, size_t b) {
    return source_key(a) < source_key(b);
});
auto first = owned_cells<T>(n);
for (size_t i = 0; i < n; ++i) first[i] = source_projection0(perm[i]);
auto second = owned_cells<U>(n); // U keeps the second column's concrete width
for (size_t i = 0; i < n; ++i) second[i] = source_projection1(perm[i]);
perm.reset(); // only if no requested permutation or later projection survives
return std::tuple{std::move(first), std::move(second)};
```

Save duplicate permutations/full sorted records and shorten scratch lifetime;
required projected columns remain resident. Account for `stable_sort`'s own
temporary allocation; this sketch makes no bounded-sort-workspace claim.
Projection purity/totality and source snapshot stability are mandatory. A demanded
permutation transfers its owner instead of resetting it. Anchor: `CodeGenExpr.fs:2502`.

## PM25 Transpose into consumer

For A of shape R×C, the logical transposed shape is C×R. A row fold over that
transpose must see the following order, even if another traversal is faster.

```cpp
auto row_results = owned_cells<T>(C);
for (size_t tr = 0; tr < C; ++tr) {
    Ordered<T, Step> row{source_seed(tr), source_step};
    for (size_t tc = 0; tc < R; ++tc)
        row.push(source_map(A(tc, tr)));
    row_results[tr] = row.finish(source_row_site(tr));
}
return row_results;
```

Target removal of `R*C*sizeof(T)` transpose storage, retaining required C
results. Initial source is stable and consumer pure/total. The current hard
transpose is a snapshot (`CodeGenBinding.fs:3381`); intervening mutation or
observed transpose output refuses replacement by reads of A. Strided bandwidth
may favor bounded packing, but packing cannot alter the fold's input sequence.

## PM26 Required transpose and assembly

The transpose itself must exist. Tile only its pure stores into a distinct final
destination; no consumer or observer sees the partly filled result.

```cpp
auto out = owned_cells<T>(checked_product(R, C)); // final shape C x R
for (size_t i0 = 0; i0 < R; ) {
    const size_t ni = std::min(tile_rows, R - i0); // positive tile sizes
    for (size_t j0 = 0; j0 < C; ) {
        const size_t nj = std::min(tile_cols, C - j0);
        for (size_t i = 0; i < ni; ++i)
            for (size_t j = 0; j < nj; ++j)
                out[(j0 + j) * R + i0 + i] = A(i0 + i, j0 + j);
        j0 += nj;
    }
    i0 += ni;
}
return out;
```

For a required rank-1 join, produce private sources directly into final slices:

```cpp
auto joined = owned_cells<T>(checked_sum(n0, n1));
for (size_t i = 0; i < n0; ++i) joined[i] = source0(i);
for (size_t i = 0; i < n1; ++i) joined[n0 + i] = source1(i);
return joined;
```

Keep source evaluation order, validation and snapshot facts; initial producers
are pure total with no extra consumers. Required output remains `N*sizeof(T)`.
Overlapping in-place transpose is excluded. Interior-axis join/stack needs its
own coordinate mapping and fresh named-axis metadata, not this flat concatenation.

## PM27 Broadcast affine operations

Keep a row-scale vector and column-bias vector small. The exact source expression
here is `(A * row_scale) + column_bias`, with a stored full result.

```cpp
auto out = owned_cells<T>(checked_product(R, C));
for (size_t r = 0; r < R; ++r) {
    const T scale = row_scale[r];
    for (size_t c = 0; c < C; ++c) {
        const T product = A(r, c) * scale;
        const T value = product + column_bias[c];
        out[r * C + c] = value;
    }
}
return out;
```

Target no replicated coefficient arrays and no product temporary. Unit/index
compatibility remains typechecked. Initial operands are stored/stable; hoisting
a source callback is not equivalent to loading a vector element. Preserve the
separate multiply/add in strict contraction-off builds. Coefficient/output alias,
cross-stage mutation or a printed product/expanded coefficient prevents elision.

## PM28 Gather terminals

Initial route is gather-to-scalar only, with pure total map/fold and immutable
source/index arrays. A validation pass preserves eager gather bounds failures
before the consumer starts; valid indices are not sorted or deduplicated.

```cpp
for (size_t k = 0; k < m; ++k) {
    const std::int64_t ix = indices[k];
    if (ix < 0 || static_cast<std::uint64_t>(ix) >= source_count)
        source_index_failure(gather_site, k, ix); // original BL8006 semantics
}
Ordered<T, Step> fold{source_seed, source_step};
for (size_t k = 0; k < m; ++k) {
    const size_t ix = static_cast<size_t>(indices[k]);
    const T value = source_map(input[ix]);
    fold.push(value);
}
return fold.finish(fold_site);
```

Target elimination of `m*sizeof(T)` gathered data; duplicate indices still
contribute repeatedly in their original order. `source_index_failure` must
render the actual source check/site, not invent a diagnostic or a new check
for statically safe `Nat` indices. Any index mutation between passes, scatter
consumer, effectful gather expression or additional snapshot consumer declines.

## PM29 Compact storage consumers

Traverse the **full logical** dense order while reading canonical storage. The
proposed `packed(lo,hi)` accessor uses the existing runtime canonical offset
for that precise compact layout; no new hand-written triangular formula is assumed.

```cpp
auto symmetric = [&](size_t i, size_t j) -> T {
    return i <= j ? packed(i, j) : packed(j, i);
};
auto antisymmetric = [&](size_t i, size_t j) -> T {
    if (i == j) return T(0); // unstored source diagonal
    return i < j ? packed(i, j) : -packed(j, i);
};
auto hermitian = [&](size_t i, size_t j) -> Complex {
    return i <= j ? packed_complex(i, j) : std::conj(packed_complex(j, i));
};
for (size_t i = 0; i < n; ++i) {
    Ordered<T, Step> row{source_row_seed(i), source_step};
    for (size_t j = 0; j < n; ++j) row.push(source_map(symmetric(i, j)));
    output[i] = row.finish(source_row_site(i));
}
```

Choose exactly the source class's accessor and element type; the loop shows the
symmetric partial-fold case. Retain canonical pool and n results, remove dense
decompaction. Signed zeros, antisymmetric diagonals and conjugation remain literal
operations. Do not replace repeated logical contributions by orbit multiplicity
times a stored cell: that changes strict arithmetic. Higher ranks require their
actual canonical read rule and logical fold nesting. Anchor: `CodeGenExpr.fs:2685`.

## PM30 Gram operator actions

This implements the explicitly requested `gram_apply(A,B,x)` algorithm, A=M×K,
B=N×K, x=N. It is not an automatic substitution for materializing `gram(A,B)`.
The example is the strict scalar route with its existing zero-seeded chains.

```cpp
auto t = owned_cells<T>(K);
for (size_t k = 0; k < K; ++k) t[k] = T(0);
for (size_t j = 0; j < N; ++j) { // existing contiguous traversal of each B row
    const T xj = x[j];
    for (size_t k = 0; k < K; ++k) {
        const T term = source_conjugate(B(j, k)) * xj;
        t[k] = t[k] + term;      // each accumulator still sees j in ascending order
    }
}
auto y = owned_cells<T>(M);
for (size_t i = 0; i < M; ++i) {
    T acc = T(0);
    for (size_t k = 0; k < K; ++k) acc = acc + A(i, k) * t[k];
    y[i] = acc;
}
t.reset();
return y;
```

`source_conjugate` is identity for real T, conjugation for the source complex
type. Complex allocation uses the normal typed owner, not PM07's arithmetic-only
uninitialized route. Storage is K workspace and M required result cells, not M*N.
The j-outer/k-inner loop preserves the existing unit-stride B reads; exchanging
those loops adds strided reads without reducing this workspace.
Preserve the selected library/FP route in comparisons. JVP/VJP reuse requires
separate derivative rules, accumulation-order proof and primal/seed alias checks.
Current implementation anchor: `CodeGenExpr.fs:3708`.

## PM31 Tensor mode products

Explicit source stages: T1[p,j,k]=sum_i U[p,i]*A[i,j,k], then
T2[p,q,k]=sum_j V[q,j]*T1[p,j,k], then Y[p,q,r]=sum_k W[r,k]*T2[p,q,k].
All three scalar chains are retained. Private intermediates permit k-slabs;
stage reordering is admitted only for total pure kernels with all shapes checked.

```cpp
auto y = owned_cells<T>(checked_product(P, Q, R));
for (size_t p = 0; p < P; ++p)
    for (size_t q = 0; q < Q; ++q)
        for (size_t r = 0; r < R; ++r) y[(p*Q+q)*R+r] = seed3;
auto t1 = owned_cells<T>(checked_product(J, block_k)); // block_k > 0
auto t2 = owned_cells<T>(block_k);
for (size_t k0 = 0; k0 < K; ) {
    const size_t nk = std::min(block_k, K - k0);
    for (size_t p = 0; p < P; ++p) {
        for (size_t j = 0; j < J; ++j)
            for (size_t h = 0; h < nk; ++h) {
                T s = seed1;
                for (size_t i = 0; i < I; ++i) s = s + U(p,i)*A(i,j,k0+h);
                t1[j*block_k+h] = s;
            }
        for (size_t q = 0; q < Q; ++q) {
            for (size_t h = 0; h < nk; ++h) {
                T s = seed2;
                for (size_t j = 0; j < J; ++j) s = s + V(q,j)*t1[j*block_k+h];
                t2[h] = s;
            }
            for (size_t r = 0; r < R; ++r) {
                T s = y[(p*Q+q)*R+r];
                for (size_t h = 0; h < nk; ++h) s = s + W(r,k0+h)*t2[h];
                y[(p*Q+q)*R+r] = s;
            }
        }
    }
    k0 += nk;
}
return y;
```

Target `(J+1)*block_k*sizeof(T)` workspace plus mandatory Y, replacing full
T1/T2. Each output's k order remains ascending across slabs; no block subtotal
is combined. Seeds here are stable scalar source values, not effects replayed
per tile. Printed/reused T1/T2, changed contraction order, or library-route
substitution blocks this admission. N09/PM31 keep one speed-plan owner.

## PM32 Factor once and stream RHS

Factors/pivots are existing immutable values with a proven valid-factor origin.
For the current scalar plain solve route, fill the required output column directly;
the order below mirrors
`materializeLuSolveForm`, `src/CodeGenExpr.fs:4226–4278`.

```cpp
for (size_t rhs = 0; rhs < right_hand_sides; ++rhs) {
    T* x = required_solution_column(rhs); // n cells, owned final destination
    for (size_t i = 0; i < n; ++i) x[i] = source_rhs(rhs, i);
    for (size_t k = 0; k < n; ++k) {
        const size_t p = static_cast<size_t>(pivots[k]); // original scalar operation
        if (p != k) std::swap(x[k], x[p]);
        for (size_t i = k + 1; i < n; ++i)
            x[i] = x[i] - LU(i,k) * x[k];
    }
    for (size_t kk = n; kk > 0; --kk) {
        const size_t k = kk - 1;
        T s = x[k];
        for (size_t j = k + 1; j < n; ++j) s = s - LU(k,j) * x[j];
        x[k] = s / LU(k,k);
    }
}
```

`required_solution_column` addresses a disjoint contiguous final column or a
row-oriented result whose declared layout matches it; otherwise use one n-cell
scratch and copy into the required strides. The cited plain scalar route casts the
pivot; it does not implement the LAPACK arm's BL8007 check. This sketch therefore
requires proven valid factors/pivots and declines unknown or malformed tuples.
Adding scalar factor validation would be separate semantics/diagnostic work.
Do not modify LU/pivots. Stream RHS acquisition only with a suitable read/order
contract; failures may require retaining the original acquisition schedule.
The transposed route separately preserves U-transpose forward substitution,
L-transpose backward substitution, then reverse pivot swaps (`:4280–4298`).
LAPACK comparisons keep that route instead of silently replacing it with this
scalar sketch. Required solutions remain stored; terminal/sink variants have
separate lifetime contracts. Zero-length cases retain source behavior; no new invalid-
factor behavior is claimed for this valid-factor scalar admission.
