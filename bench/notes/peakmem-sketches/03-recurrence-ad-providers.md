# Recurrence, derivative and provider implementation sketches

Status: SKETCH ONLY, 2026-10-21. These are uncompiled, unexecuted emitter/runtime
fragments against the inspected `667250cd` design baseline. They are not production
changes or new Blade syntax. Imperative loops below are generated C++ shapes, never
source-language idioms. All helper names without an existing source anchor are proposed
stubs; resource/window/spool contracts belong to [00-region-runtime](00-region-runtime.md).

This file covers PM33–PM48 and PM59 from the [parent plan](../peakmem-microkernel-plan.md).
The [streaming design](../peakmem-streaming-optimizer-plan.md) and its
[review](../peakmem-streaming-review-claude.md) govern provider capability, planner ownership,
failure ordering and disk policy. Every admission also needs the complete demand/alias/
effect/totality audit; a matching expression shape alone never authorizes the sketch.

All provider sketches inherit R06's whole-read validation boundary, including halo,
projection and row-normalization variants. Per-window validation alone is insufficient.
Outputs remain private until those checks finish; earlier source failures cannot be
deferred across later potentially failing reads. R08/ST16 govern every public sink's
commit/cleanup outcome, including uncertain publication.

## PM33 — Final-state bounded-lag recurrence

Target: current recurrence desugaring in `TypeCheckInfer.fs`; one shared port with speed
N01, not an independent `Optimize.fs` peephole after a trajectory has already disappeared.
Admit fixed step count, constant positive prefix lags at most K, no AD/guard, and only
proved final/trailing demands. K=0 permits a single slot when the body never reads prefix.

```cpp
// Proposed emitted shape: seed plan retains original arm selection and timing.
auto ring = zero_rows<T>(checked_add(K, 1), C, budget);
auto zero = readonly_zero_row<T>(C, budget);
ordinal built = 0;
auto prefix_read = [&](ordinal j) -> BorrowedWindow<const T> {
    if (j < 0 || j >= built) return zero.borrow(); // existing zero-history guard
    assert(built - j <= K);                     // classifier proof, not a guard
    return ring.borrow(j % (K + 1));
};
for (ordinal t = 0; t < Tsteps; ++t) {
    auto dst = ring.exclusive_row(t % (K + 1));
    // A reused slot contains an older row, not the original fresh-row defaults.
    if (!coverage.proves_complete_overwrite(seed_plan.arm_at(t)))
        initialize_original_row_defaults(dst);
    if (seed_plan.has_arm(t)) emit_original_seed_into(t, dst, prefix_read);
    else emit_original_step_into(t, dst, prefix_read);
    built = t + 1;                              // only a completely written row
    consume_admitted_row(t, dst.as_readonly());  // no borrower survives overwrite
}
```

The zero guard precedes modulo/index formation. Seeds and partial-write initialization
retain their original semantics; zeroing the ring is not a proof of complete writes.
`initialize_original_row_defaults` writes the exact initial values that a fresh full
trajectory row would have before that arm executes, on every reuse. Its omission needs
complete logical write coverage for that arm; neither allocation-time zeroing nor a
previous arm's coverage proves this. An unsupported initialization contract declines.
Replace every conceptual array use, then dissolve its original name; never expose a
`K+1`-extent array as the original T-extent value. An escaping final row must own a copy
or pin/transfer its root. Decline dynamic reads, dense demand, whole printing and any
unreplaced hidden use. Payload is `(K+1)C` cells plus zero/snapshot/output state, not RSS.

## PM34 — Sparse recurrence snapshots

Target: PM33's demand classifier and statement splice. Normalize static demanded
ordinals; share a snapshot for repeated requests, preserving distinct downstream views.
The coverage gate compares ring plus distinct owning snapshots with full materialization.

```fsharp
// Proposed classifier output; all embedded extents/captures/prints are demands.
let requested = demands |> staticOrdinals |> Set.ofList
let residentRows = checkedAdd (checkedAdd maxLag 1L) (int64 requested.Count)
if hasUnknownDemand || residentRows >= steps || denseCoverage residentRows steps then
    Full "snapshot demand defeats rolling storage"
else
    Windowed { MaxLag = maxLag; Snapshots = requested; OriginalExtent = steps }
```

```cpp
// In consume_admitted_row(t, row), after seed/step has completed:
if (snapshot_plan.contains(t)) {
    auto owned = snapshot_slots.exclusive(t); // reserved once; exact trailing type
    copy_logical_cells(row, owned);           // before ring slot can be overwritten
    snapshots.publish(t, owned);              // stable owner, never a borrowed ring row
}
// Later NAME(p, rest...) becomes snapshots.owner(p).index(rest...).
```

Keep snapshots through their last actual borrower, including returned views and delayed
captures; no copying on every repeated read. Include snapshot metadata and root pinning
in the bound. A dynamic ordinal, full-history observer or guard/freeze still declines.
T=0, seed-only trajectories and out-of-range demanded ordinals preserve existing
diagnostics/zero semantics; do not route an invalid ordinal through a missing snapshot.

## PM35 — Statistics riding a recurrence

Target: after PM33's classifier is independently sound, fuse an exact terminal recipe
into row completion. The source's rank and fold nesting determine the state machine;
do not flatten a row reduction followed by an outer reduction into one scalar chain.

```cpp
auto outer = init_exact_outer_seed_at_original_point();
auto on_completed_row = [&](ordinal t, BorrowedWindow<const T> row) {
    auto inner = init_exact_row_seed(t);
    for (ordinal c : original_inner_order(C))
        inner = emit_original_inner_fold(inner, original_map(row.at(c)));
    auto row_stat = finish_exact_inner_fold(inner); // retains empty-row handling
    outer = emit_original_outer_fold(outer, row_stat);
    snapshots.capture_if_demanded(t, row);
};
emit_ring_sweep_with(on_completed_row);
auto result = finish_exact_outer_fold(outer);
```

Each extra statistic owns its seed and arithmetic graph; a joined traversal does not
merge fold licences. Carry a full-axis scalar chain across rows only when that is the
original lowered fold; otherwise retain its original nested accumulator states.
Initially require recurrence, seeds, maps and terminals to be pure/total, with identical
required validation order. Otherwise an early diagnostic could preempt a later recurrence
failure. Storage is the ring plus statistic/snapshot state; include array accumulators.
Guarded recurrence freeze and its repeated frozen diagnostic rows remain a later feature.

## PM36 — Spatial and temporal stencil slabs

Target: PM33/N01 plus existing halo lowering. First sketch a pure Jacobi time block,
with no demanded intermediate times inside that block. Required times split blocks.
The bounded slab replaces trajectory history; two full time-boundary grids may remain.

```cpp
for (ordinal t0 = 0; t0 < updates; ) {
    auto depth = depth_for(t0); // clipped by next required snapshot and final time
    require(depth > 0 && depth <= updates - t0);
    auto next_grid = boundary_grid_destination();
    for (auto core : spatial_cover(original_domain)) {
        auto reach = checked_mul(radius, depth);
        auto initial = intersect(checked_expand(core, reach), original_domain);
        auto slab = load_physical_cells(cur_grid, initial, budget);
        for (ordinal s = 1; s <= depth; ++s) {
            auto remaining = checked_mul(radius, depth - s);
            auto targets = intersect(checked_expand(core, remaining), original_domain);
            auto next = slab_scratch.exclusive(targets, budget);
            auto reads = physical_boundary_reader(
                slab, original_domain, t0 + s - 1, original_boundary_rule);
            emit_exact_jacobi(reads, next, original_boundary_rule, t0 + s);
            reads.release(); // release the input slab's borrow before recycling it
            slab = swap_and_release_previous(slab, next);
        }
        copy_core_to_final_destination(slab, core, next_grid);
    }
    cur_grid = finish_boundary_grid(next_grid); // all cores complete before next block
    t0 += depth;                               // checked/clipped above
}
```

Physical boundaries apply at each stage in global coordinates, never at tile seams.
Targets come from the remaining dependency expansion, not shrinking a slab that was
already clipped at a physical edge: boundary cells remain targets through the last step.
`physical_boundary_reader` supplies the original out-of-domain rule at the input time;
the emitter also applies any prescribed output boundary value at the output time. Every
mapped in-domain read must be covered by the slab. This first geometry declines periodic
wrap or other remote boundary mappings unless their full mapped dependency cover is proved
and represented; pretending a missing mapped cell is a tile-edge boundary is forbidden.
Decline Gauss–Seidel, red-black, hidden writes, may-fail recomputation, nonlocal accesses
and steps whose dependency radius is not proved. Overlap recomputation preserves each
cell's original arithmetic but adds work; report it. Budget includes both boundary grids,
two maximal halo-expanded slabs, snapshots and decoder scratch. A row/plane ring within
one step is a separate narrower schedule, not evidence that all time tiling is legal.

## PM37 — Multiple FIRs over chunks

Target: ordinary C++ streaming apply in `CodeGenCuda.fs`, despite its filename. Begin
with one immutable supported provider, two pure filters and identical output demand.
PM42 supplies bounded ghost reuse; resident twofir does not establish this capability.

```cpp
for (auto core : logical_windows(domain, block_shape)) {
    auto needed = union_of_filter_halos(core, filter_specs);
    auto input = halo_cache.read_window(snapshot, needed, budget);
    auto out_a = output_a.acquire(core); // resident final slice or staged sink block
    auto out_b = output_b.acquire(core);
    for (auto i : core.original_order()) {
        out_a.at(i) = emit_filter_a_exact(input, i); // original tap order and seed
        out_b.at(i) = emit_filter_b_exact(input, i);
    }
    output_a.accept(core, out_a);
    output_b.accept(core, out_b);
    input.release();
    halo_cache.evict_outside(next_required_halos(core));
}
```

Distinct filter radii use their union, including edge policy; no padding beyond the
declared physical domain masquerades as real samples. Both resident outputs still cost
their full payload. For sinks use PM45's staging/publication barrier. Decline inter-filter
effects, escaped windows, unsupported codecs and byte bounds based only on useful core
cells. Include ghost capacity, codec chunks, output blocks and all live consumer pins.

## PM38 — Destination cotangent accumulation

Target: AD contribution generation and the emitter, with the caller's existing gradient
preserved. For a reference `g[j] + ((0 + a[j]) + b[j])`, direct `g[j] += contribution`
changes parentheses. This conservative tile sketch preserves the subtotal and final add.

```fsharp
// Admission classifies the ORIGINAL derivative graph, not a preferred rewrite of it.
match classifyOriginalAdjointAccumulation derivativeGraph with
| ZeroSeededSubtotalThenOneIncomingAdd recipe when replayIsPureTotal recipe ->
    TiledSubtotal recipe
| _ -> RetainOriginalAccumulation "subtotal/final-add graph is not the original graph"
```

```cpp
// PRE: admitted recipe has an exact zero-seeded subtotal and ONE final incoming-g add.
for (auto targets : target_tiles(gradient_domain, B)) {
    auto subtotal = exact_zero_tile(targets, budget);
    for (auto event : original_adjoint_event_order()) {
        auto j = original_target(event);
        if (targets.contains(j)) {
            auto contribution = emit_original_contribution(event);
            subtotal[j] = subtotal[j] + contribution;
        }
    }
    for (auto j : targets.original_order())
        gradient[j] = gradient[j] + subtotal[j]; // including untouched subtotal zeros
}
```

`original_adjoint_event_order` means the existing derivative's order, not an assumed
reverse traversal of every loop. Duplicate targets keep their exact subsequence and
signed/compact factors. The scan can cost `ceil(N/B)` replays; claim bounded storage,
not a speed win. Admit only replayable total contributions with immutable captured
primals/seed and no alias to the destination; otherwise retain the full subtotal.
Fresh gradients, atomics and disjoint-owner specializations need separate proofs.
Skipping the final `g + 0` can even change signed zero. Count tile/worker multiplication.
If the original graph is `((g + a) + b)`, this zero-subtotal sketch is refused; preserve
that incoming-gradient chain with its own exact schedule instead. Neither a matching
set of contributions nor unchanged event order permits replacing one graph with the other.

## PM39 — Sparse cotangent initialization

Target: internal fresh cotangents only. An epoch descriptor gives unvisited cells their
logical zero without first touching the value pool. It is not the caller's accumulating
gradient ABI and does not itself provide sublinear allocation: epoch metadata is O(N).

```cpp
template<class T, std::unsigned_integral epoch_t = uint64_t>
struct ProposedFreshCotangent {
    span<T> values; span<epoch_t> seen; epoch_t epoch = epoch_t{1};
    ProposedFreshCotangent(span<T> storage, span<epoch_t> tags)
        : values(storage), seen(tags) {
        require(values.size() == seen.size());
        std::fill(seen.begin(), seen.end(), epoch_t{0}); // reserved invalid sentinel
    }
    T read(index j) const { return seen[j] == epoch ? values[j] : exact_zero<T>(); }
    void add(index j, T contribution) {
        T old = read(j);
        values[j] = old + contribution; // retain even the first zero + contribution
        seen[j] = epoch;
    }
    void begin_fresh_epoch() { // only after every reader of the previous epoch finishes
        if (epoch == std::numeric_limits<epoch_t>::max()) {
            std::fill(seen.begin(), seen.end(), epoch_t{0});
            epoch = epoch_t{1};
        } else {
            ++epoch;           // zero is never a valid epoch, including after wrap
        }
    }
    void expose_dense(span<T> output) const {
        require(output.size() == values.size());
        for (index j = 0; j < output.size(); ++j) output[j] = read(j);
    }
};
```

All internal readers must use the descriptor; bare pointers/callbacks force a dense
materialization and its zero stores before escape. Never read an uninitialized value
before testing its initialized tag. A touched-list/hash alternative may reduce metadata
at low density, but needs a proved bound or checked fallback; hash iteration cannot
reorder accumulation. Existing caller buffers preserve their prior cells. Compare traffic
savings separately from peak bytes: tags plus value capacity may be worse than a zero sweep.
The initial tag fill is mandatory even when the value pool remains uninitialized. Epoch
advance is an exclusive lifecycle operation, never a way to invalidate a live borrower.

## PM40 — Recurrence AD checkpoints

Target: a later AD-specific schedule, not PM33's initial no-AD ring. Fixed steps, pure
total replay, immutable captured state, fixed FP/distribution versions, and the exact
existing reverse recipe are prerequisites. A checkpoint stores the complete K-row state.

```cpp
auto checkpoint_times = fixed_spacing_schedule(updates, segment_steps);
forward_exact([&](ordinal t, const RecurrenceState& state) {
    if (checkpoint_times.contains(t)) save_checkpoint(t, deep_copy_state(state));
}); // required snapshots/primal outputs are retained separately
auto adjoint = seed_exact_terminal_adjoint();
for (auto segment : checkpoint_times.reverse_segments()) {
    // Immutable owner/pin contains the K required rows preceding this segment.
    auto boundary = restore_immutable_checkpoint(segment.begin, budget);
    auto state = copy_mutable_replay_state(boundary, budget);
    auto local_tape = reserve_segment_tape(segment.length(), budget);
    for (ordinal t = segment.begin; t < segment.end; ++t)
        replay_original_step_and_save_needed_primals(t, state, local_tape);
    state.release(); // tape records own their saved values, not borrows into this state
    auto primals = borrow_segment_primals(boundary, local_tape, original_zero_history_rule);
    for (ordinal t = segment.end; t-- > segment.begin; )
        emit_original_recurrence_reverse_step(t, primals, adjoint);
    primals.release();    // release both boundary and local-tape pins before either owner
    local_tape.release(); // adjoint boundary state carries into the previous segment
    boundary.release();  // checkpoint storage itself stays live if another owner needs it
}
emit_original_seed_reverse_once(adjoint);
```

The schedule API defines checkpoints as complete states before update t; seeds and the
final boundary are handled once. For lag K, replay/adjoint boundary state includes all
required lags, not merely one row. Bound checkpoint payload plus the largest local tape
and reverse scratch, including the mutable K-row replay copy and any restored boundary
copy. Restoring may instead pin an existing checkpoint; charge each unique allocation
once and keep that pin throughout the segment's reverse sweep. Never replay a segment
by reconstructing the full trajectory.
`borrow_segment_primals` resolves original primal requests against the immutable K-row
boundary for times below `segment.begin`, and against the owned per-update tape records
for this segment. Each record retains every needed pre/post-update value, including the
last update's required outputs. The accessor applies the original prefix/prehistory
zero guard before lookup, so it never exposes a saved future row to an earlier prefix.
The boundary is not the mutable state advanced during replay. Its complete lag set,
zero rule and accessor lifetime must be proved; unsupported requests decline.
Do not replay caller-gradient writes. Guards, external reads, mutation, escaping primal
views and unsupported reverse-order patterns decline. `_at` RNG needs PM47's full identity.

## PM41 — Chunked map to fold

Target: `CodeGenBinding.fs` streamed fold plus a new region block-consumer path. Begin
with one provider with verified block capability (the plan's first lane is Zarr/C++),
immutable snapshot, pure/total maps and the provider's original validation/read order.

```cpp
auto acc = init_exact_seed_at_original_point();
auto reader = begin_original_read(snapshot, original_read_contract, budget);
auto cursor = logical_block_cursor(domain, provider_geometry, compute_shape);
while (auto request = cursor.next()) {          // arithmetic/paged cursor, not N chunks
    auto input = TRY(reader.read_block_recording_validation(*request));
    for (auto i : request->original_fold_order())
        acc = emit_exact_fold_step(acc, emit_exact_map(input.at(i)));
    input.release();
}
TRY(reader.finish_original_validation_and_close());
auto result = finish_exact_fold(acc);           // unseeded empty: original BL8003
```

Carry one strict accumulator across blocks, never chunk sums combined afterward. Rank-2
partial folds use one state per active row in the exact nesting order; full rank-2 folds
must preserve their own original nesting. Count compressed fetch, decoded chunk, convert
scratch and metadata capacities. A codec requiring a full chunk sets the minimum buffer.
Multiple source validation barriers, unsupported `.stream` escapes and unknown byte
bounds decline; current interpreter/LLVM provider refusals are not implementation targets
of this sketch. Increasing chunk count must not grow a retained block/run table.
The proposed reader owns validation state for the original read operation. In particular,
`ProviderRegistry.fs:204` accumulates missing count and first flat coordinate during
chunked assembly and reports after its loops; a block-local refusal is not equivalent.
Record those observations without raising early, preserve intervening I/O/decode failure
order, then finish the original checks before the result is exposed. An original
per-block validation boundary instead remains per-block; an adapter unable to reproduce
the original boundaries declines. The checked close also releases all decoder ownership.

## PM42 — Halo chunk residency

Target: bounded shared decoded-chunk cache under `BorrowedWindow`. Geometry creates
requests; snapshot identity and chunk IDs identify backing. Only active/read-ahead chunks
and the metadata necessary to locate them remain in RAM.

```cpp
auto needed = checked_chunk_cover(logical_core.expanded(halo), geometry);
cache.evict_unpinned_outside(needed); // pinned old ghosts remain live and charged
auto missing = cache.missing_chunks(needed, snapshot.identity()); // active set only
auto extra = checked_transition_capacity(missing, codec); // entries + one decode workspace
auto pending = TRY(budget.reserve_bundle(extra.total_bytes));
auto workspace = TRY(allocate_decode_workspace(pending.take(extra.workspace_bytes)));
for (auto chunk : missing.in_provider_validation_order()) {
    auto key = SnapshotChunkKey{snapshot.identity(), chunk.id};
    auto credit = pending.take(chunk.owned_capacity_bytes()); // payload + entry metadata
    auto entry = TRY(decode_chunk_exact(snapshot, chunk, move(credit), workspace));
    cache.insert_owned(key, move(entry)); // entry retains its allocation AND reservation
}
workspace.release();              // frees workspace before releasing its reservation
pending.release_unused_credit(); // transferred entry credits remain charged in cache
auto window = cache.borrow_logical_window(logical_core, halo, needed);
consume_synchronously(window);
window.release();
cache.evict_unpinned_outside(next_window_chunk_cover());
```

Reserve the transition peak: pinned old ghosts plus newly decoded chunks can coexist.
`checked_transition_capacity` counts only new entry allocations and peak compressed/decode
scratch; existing cache entries are already charged in the execution-wide ledger. Thus
the reservation checks existing live roots plus this incremental transition bundle,
without charging cache hits twice. A decoded entry owns its transferred reservation
until eviction actually frees the backing; failure cleanup retains credits until any
partial allocation is gone. A window's token pins the entry owner, not just a raw pointer.
If that exceeds the declared minimum, refuse/choose a smaller proven schedule before
waiting for memory; do not deadlock behind a still-pinned window. Reuse immutable chunks
only, with boundary fills represented explicitly. Irregular run/chunk lookup metadata
needs a bounded/paged lookup contract; O(number of chunks) directories are a growing term.
Prefetch comes later with bounded compressed bytes, decoded bytes and reorder entries.

## PM43 — Decode into final destination

Target: provider adapter capability plus existing resident-array construction. The final
array is mandatory; eliminate duplicate decode/conversion/assembly pools where the codec
can target a correctly typed, aligned and strided destination safely.

```cpp
auto output = reserve_unpublished_final_array(type_and_axes, budget);
auto reader = begin_original_read(snapshot, original_read_contract, budget);
for (auto chunk : original_provider_order(snapshot)) {
    auto dst = output.exclusive_region(chunk.logical_region());
    if (capability.decode_exactly_into(dst.layout(), output.element_type())) {
        TRY(reader.decode_into_recording_validation(chunk, dst, bounded_codec_workspace));
    } else {
        auto tmp = TRY(reader.read_block_recording_validation(chunk.logical_region()));
        emit_original_conversion_and_scatter(tmp, dst);
        tmp.release();
    }
    output.mark_initialized(chunk.logical_region());
}
output.require_complete_coverage();
TRY(reader.finish_original_validation_and_close());
auto published = output.finish();
```

`decode_into_recording_validation` is a proposed stronger capability, not inferred from
a read hook. Both destination and fallback routes feed the same original-read validation
state exactly once per required source cell. Deferred missing counts/first coordinates
finish at the original whole-read boundary, as in PM41; neither branch raises a deferred
missing error on the first bad block or skips final validation before `output.finish`.
Failure may leave private destination bytes dirty; no reader/view/print sees them before
success. Preserve missing/nonfinite/endianness conversion checks, their source order and
edge-chunk extents. Initial conversion is total; a fallible conversion retains its original
validation barrier. A codec owning or freeing its output pointer cannot be handed Blade's
pool without an explicit ownership interface. Bound is full output plus maximum codec/
fallback workspace, metadata and overlaps; no blanket zero-fill removal without coverage.

## PM44 — Provider selection and projection

Target: region demand propagation and lane-neutral provider facts. It is not safe to
elide column/chunk reads simply because their values are unused: their validation may be
observable. Begin with an explicitly projected provider contract or equivalent certificate.

```fsharp
// Proposed Optimize-facing capability facts, not a ProviderRegistry call.
match providerFacts.TryProject(requiredColumns, requiredWindows) with
| Some proof when proof.PreservesValidationTrace && predicatesArePureTotal ->
    RegionRead { Projection = proof.PhysicalReadPlan
                 Snapshot = pinnedIdentity; Order = proof.LogicalOrder }
| _ -> RetainOriginalRead "unproven skipped-cell validation or unsupported projection"
```

```cpp
auto reader = begin_projected_read(snapshot, proof, original_read_contract, budget);
for (auto window : projected_cursor.in_original_order()) {
    auto columns = TRY(reader.read_projected_block_recording_validation(window));
    for (auto row : window.original_order())
        if (exact_predicate(columns, row)) terminal.accept(exact_projection(columns, row));
    columns.release();
}
TRY(reader.finish_original_validation_and_close()); // before terminal publication
```

`read_projected_block_recording_validation` is a future capability, not claimed for
CSV/NetCDF today.
Selected arrays retain stable order, output capacity and required index metadata; scalar
terminals may omit them under PM19. Chunk co-location can force decoding unused columns,
so selected-row fraction is not a fetched-byte ratio. A prevalidated snapshot certificate
must cover the omitted checks and identity; size/mtime alone is not such a certificate.

## PM45 — Producer into provider sink

Target: `genProviderWriteBinding` and new provider writer capabilities. Existing dense
alias writes are the control; `GenWriteVar` accepting a whole array is not a chunk sink.
All output remains private until complete validation, including existing BL8015 policy.

```cpp
struct ProposedWriteTally {
    uint64_t count = 0;
    std::optional<uint64_t> first_flat;
    void note(uint64_t original_writer_flat_index) {
        count = checked_add(count, uint64_t{1});
        first_flat = first_flat ? min(*first_flat, original_writer_flat_index)
                                : original_writer_flat_index;
    }
};
auto stage = TRY(CheckedSpool::create(policy, output_schema, budget));
auto producer = begin_admitted_producer(original_recipe, budget);
ProposedWriteTally write_check;
for (auto window : output_logical_windows()) {
    auto values = TRY(producer.produce_exact_window(window));
    for (auto cell : original_writer_storage_cells(window, values)) {
        if constexpr (original_writer_checks_nonfinite<T>) { // current float/double arms
            if (!std::isfinite(static_cast<double>(cell.value)))
                write_check.note(cell.original_flat_index);
        }
    }
    TRY(stage.append(window, values)); // synchronous copy, no public provider mutation
    values.release();
}
TRY(producer.finish_original_validations());
TRY(stage.seal_and_verify_complete());
if (write_check.count != 0)
    TRY(original_BL8015_failure(write_check.count, *write_check.first_flat,
                               var_name, store_path));
auto transaction = TRY(sink.begin_unpublished_write(output_schema));
auto replay = TRY(stage.open_bounded_checked_reader(budget));
while (auto values = TRY(replay.next_checked_block())) {
    TRY(transaction.write_chunk_synchronously(values.logical_window(), values));
    values.release(); // writer has consumed/copied the block; no dangling retained borrow
}
TRY(replay.finish_and_check_footer());
TRY(replay.close_checked());
TRY(transaction.finish_and_validate());
auto outcome = transaction.commit_with_provider_contract();
handle_commit_outcome(outcome, transaction, stage); // exact R08/ST16 state machine
```

This is gated on a real provider publication contract; a generic filesystem rename does
not prove CSV/NetCDF/Zarr equivalence. Required resident output stays resident as well.
Preserve any original computation/validation ordering with total kernels and capability
proofs; do not promote later semantic failures earlier by interleaving unchecked stages.
Scratch/disk-quota/I/O resource failures belong to the declared new policy, not a claim
of identical allocation-failure behavior. Budget/disk bounds include spool, encoded
transaction and codec overlap. Cleanup covers owned run directories, never unrelated files.
`original_writer_storage_cells` visits every cell in the old writer's flat buffer exactly
once, with its original flat storage ordinal; packed/canonical storage is not silently
expanded into logical duplicates. The tally reproduces `blade_runtime.hpp:481`'s full
nonfinite count and minimum flat ordinal, with the same BL8015 formatting. It observes
without raising until all original producer computation/read validations have succeeded.
`original_BL8015_failure` returns a failed result through the execution cleanup path;
it must not call the current panic helper directly while owned spool files remain live.
The producer retains every internal source-validation boundary; `finish_original_validations`
performs only its outstanding original end checks, not a deferral of earlier source errors.
Unsupported storage mappings, validation traces or asynchronous borrowed-block sinks decline.

## PM46 — Chunked row normalization

Target: a region statistic barrier choosing retain-row, certified replay or private spool.
Keep the source's actual two-pass statistic and normalization formulas; no silent online
variance/softmax substitution. Start with one row at a time and total kernels.

```cpp
for (auto row : original_row_order()) {
    auto storage = plan_row_storage(row, budget, snapshot, policy);
    auto stat = original_stat_seed(row);
    for (auto block : original_row_blocks(row)) {
        auto x = read_block(snapshot, block, budget);
        stat = continue_exact_stat_chain(stat, x);
        storage.retain_or_spool_exact_values(block, x); // no-op only for certified replay
    }
    auto summary = finish_original_stat(stat);
    storage.finish_first_pass();
    for (auto block : original_row_blocks(row)) {
        auto x = storage.read_or_replay_exact(block, budget);
        destination.accept(block, emit_original_normalize(x, summary));
    }
}
```

Do not fuse row-by-row if the source requires a global all-row validation/statistic barrier
before any may-fail normalization. First admissions need total downstream kernels and
identical source-validation trace. Replay means immutable repeatable values, fixed decode/
FP behavior and source identity stronger than size/mtime. A spool stores exact bits and
schema, not text-roundtripped numbers. Discard each row's spool only after its last borrower.
Count extra I/O, disk high water and mandatory output; PM45 handles sink publication.

## PM47 — Indexed RNG to consumer

Target: existing addressed `_at` generator semantics, then a consumer-aware emitter.
Samples are identified by original logical position, not the number of selected samples
or a chunk-local ordinal. The sequenced RNG API remains a separate task.

```cpp
validate_original_rng_parameters_once_at_original_point();
auto acc = original_consumer_seed();
for (auto block : logical_windows(sample_domain, block_shape)) {
    for (auto i : block.original_order()) {
        auto address = checked_original_address(offset, i);
        auto x = emit_same_at_distribution(key, stream, address, distribution_params);
        acc = original_consumer_step(acc, x);
    }
}
auto result = finish_original_consumer(acc);
```

`emit_same_at_distribution` means the exact existing generator/distribution algorithm,
including per-sample internal rejection draws, parameter checks and FP behavior. Do not
invent independent counters inside a distribution or scalarize a vector implementation
unless identical logical samples are established. Selection keeps original i, and a
multidimensional mapping uses the original linearization. Required random arrays still
store all cells. Replay/checkpoint identity includes key, stream, address, parameters,
algorithm version and FP policy; unsupported backend/distribution combinations decline.

## PM48 — Immutable revision reuse

Target: existing `CodeGenTiles.fs` and runtime cache identity/publication as precedents.
Keep its admitted resident-output route distinct from the proposed bounded region cache.
Each tile key includes all actual dependencies, including upstream halo chunks.

```cpp
for (auto tile : arithmetic_tile_cursor(output_domain)) {
    auto dependencies = bounded_dependency_cursor(tile, transitive_halo_recipe);
    auto key = hash_recipe_geometry_fp_and_content_ids(dependencies);
    if (auto cached = cache.try_verified_tile(key, tile.schema())) {
        destination.accept(tile, cached->borrow());
    } else {
        auto value = execute_original_tile(tile, budget);
        destination.accept(tile, value);
        cache.try_publish_verified_tile(key, value, declared_disk_quota);
    }
    cache.release_tile_pins();
}
```

A cache miss/corruption in an optional cache may recompute; a corrupt mandatory spool
must fail, not silently fabricate or re-evaluate data. Bounded dependency lookup is a
new prerequisite: current baked per-chunk tables/code size are growing metadata and
cannot support a constant-total-memory claim. Preserve validation/effects on cache-hit
paths and cache only pure admitted computations; captured values must enter identity or
decline. Report RAM cache, disk footprint and eviction together. Never invalidate only
the changed core when a halo consumer depends on neighboring chunks.

## PM59 — Function-level AD primals

Target: `Grad.fs` already recomputes loop-body lets while function-level values survive
the reverse sweep. First release backing roots after their last reverse borrower; then
consider selected rematerialization as a separately admitted replay schedule.

```fsharp
// Proposed post-AD lifetime analysis includes primal outputs and side-table operands.
let uses = collectCompleteForwardReverseAndSideTableUses generatedDerivative
let roots = resolvePrimalAliasesVersionsAndEscapes generatedDerivative uses
for root in roots do
    if cheap root.Recipe && pureTotalRepeatable root.Recipe &&
       immutableCapturesAtOriginalVersion root.Recipe && not root.Escapes then
        offerReplayCandidate(root, exactReverseUseSites root uses)
let selected = selectRetentionAndReplaySchedule generatedDerivative
clearStaleLifetimeAnnotations selected
let finalUses = collectCompleteForwardReverseAndSideTableUses selected
let finalRoots = resolvePrimalAliasesVersionsAndEscapes selected finalUses
for root in finalRoots do
    if root.Escapes then
        // Return/capture transfers or retains ownership; it is not a local last-use free.
        attachOwnerTransferOrPins root (allEscapeSites root finalUses)
        forbidLocalBackingFree root
    else
        let last = lastActualBorrowerIncludingReplayAndDeferredUses root finalUses
        attachReleaseAfter last root
```

```cpp
auto needed_primal = replay_exact_recipe_from_pinned_versions(); // if selected
emit_original_reverse_operation(needed_primal, cotangents);      // unchanged ordering
release_when_last_root_borrower_finishes(needed_primal);
// At an escaping primal view's return/capture site, using the admitted ABI owner carrier:
auto escaped = bind_view_with_transferred_or_pinned_owner(primal_view, primal_root);
publish_return_or_capture(move(escaped)); // receiving lifetime owns the root reservation
```

Releasing a view does not free its still-borrowed root. Replay may need more pinned
dependencies than the value it removes; compare combined peaks, not individual sizes.
Recompute releases from the selected schedule; a replay candidate invalidates old liveness.
`attachOwnerTransferOrPins` preserves backing through every escaping result/capture and
all local borrowers; local handles may be dropped only after transfer/pinning succeeds.
Borrowed caller roots retain their existing caller ownership rather than being freed by
the derivative. If the boundary cannot carry the necessary existing ownership/pin contract,
decline the lifetime optimization. No local last-use rule frees an escaping root at its
return/capture statement. Nonescaping roots include actual forward, reverse, replay and
deferred uses in their final liveness census; pinned replay dependencies count in the bound.
An external read, modified capture, branch decision requiring a saved trace, may-fail
expression or expensive library call defaults to retention. Repeated replay never
repeats caller-gradient mutations. Preserve original forward failures and reverse
accumulation parentheses; changing checkpoint/replay strategy is not permission to
reorder the derivative. The returned primal remains a required output.
