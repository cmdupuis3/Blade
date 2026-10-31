# Eighteen composed pipeline sketches

Sketches only; not compiled, emitted or executed. These are proposed internal schedules,
not new Blade syntax. The pseudocode operations below have these contracts:

- `read/produce(window)` returns a bounded borrowed block with an exact logical origin.
- `fold_in_order` advances the original seeded chain, never a sum of block summaries.
- `release` waits for every borrower; all queue/decode/metadata capacity is charged.
- `spool` is the private checked sequential store in [R07](00-region-runtime.md#r07-checked-sequential-spool-across-a-statistic-barrier).
- Every interleaved compute kernel is pure/total in the initial admissions. Provider
  validation retains its prescribed order; unsupported order proofs decline the route.
- `finish_read_at_original_boundary` completes the provider's whole-read checks: exact
  extent/record count, required excluded-data checks, final decoder/footer/integrity and
  close checks, and any whole-array validation/diagnostic the original read performs.
  It is not equivalent to decoding the last requested window. Where that read reports
  a full nonfinite tally/first storage cell, retain those exact bounded summary fields.

Every provider-backed variant below must finish each read at its original semantic
boundary before publishing a result or raising a later terminal/empty-fold diagnostic.
If source A originally finishes before potentially failing source B, merely finishing
A then B after an interleaved scan is insufficient: prove the interleaving preserves
that failure order, perform a legal validation stage, or decline. Total private arithmetic
can run earlier only under the admission proof; its results remain unpublished. A demand
that skips data does not skip required validation. These rules also apply when a provider
is substituted into a section whose example starts from resident input.

Each section composes PM sketches. A required resident output is allocated and retained;
a terminal-only result cannot stand in for that contract. References to external algorithms
below are later proposals with explicit gates, not routes currently available in Blade.

## ST01 Stored maps to terminal

```text
discover input-version -> map f -> map g -> seeded fold h
if an intermediate is printed/returned/captured/indexed: retain its materialization
else if all moved kernels/seeds total and pilot disjoint from legacy planners:
    acc = original seed at its admitted position
    for each logical input coordinate in original order:
        u = f(input[coordinate])
        v = g(u)
        acc = h(acc, v)
    return acc
```

This should reuse current deferred terminals where possible; only a missing admission
justifies a new optimizer route. PM01/09/10 provide storage and partial-fold variants.

## ST02 One provider to joined statistics

Use the concrete C++ loop in [R06](00-region-runtime.md#r06-st02-connect-a-block-reader-to-map-recipes-and-joined-folds):
one rank-1 Zarr block reader, total scalar recipes, independent typed seeds/accumulators.

```text
reserve input block + full decoder unit + fixed metadata + each fold state
for arithmetic source block [lo,min(lo+B,N)):
    read/validate block in source order
    for each cell: evaluate shared producer once; advance every original fold leg
finish_read_at_original_boundary(source)
publish tuple only after successful whole-read finalization
```

The missing block-consumer emitter is new work. A joined fiber fallback that rejects
rank 1, or an ordinary map route that stores N cells, is not completion. PM11/41/43.

## ST03 Two chunk geometries to zip and selected statistics

```text
require identical logical axes/extents and admitted cross-source validation ordering
require compute block size compute_cells > 0
cursor = 0
while cursor < N:
    borrow cached A_window/B_window decode windows that cover cursor
    require each borrowed window has origin <= cursor < end
    next_compute = cursor + min(compute_cells - (cursor mod compute_cells), N - cursor)
    stop = min(A_window.end, B_window.end, next_compute, N)
    require stop > cursor before consumption; otherwise fail the provider/window contract
    for i in [cursor, stop):
        if predicate(A_window.at(i), B_window.at(i)):
            acc = original_step(acc, producer(A_window.at(i), B_window.at(i)))
    release windows only after their last use; cursor = stop
finish A/B whole reads at the source boundaries admitted above
publish acc only after both whole-read finalizations succeed
```

Bound cached decode units and alignment metadata; do not allocate an LCM-sized block.
Two source readers with incompatible failure order must stay outside this route. PM19/41/44.

## ST04 Synchronized fanout

```text
for each source block:
    fill one shared produced block (or one scalar value when all consumers inline)
    consumer A advances its own chain in logical order
    consumer B advances its own chain in logical order
    required output C stores its corresponding cells
    release produced block after A, B and C finish
finish_read_at_original_boundary(source), when provider-backed
publish A/B and expose C only after required validation succeeds
```

This moves evaluations between consumers, so totality and effect-free captures matter even
though each fold's arithmetic stays ordered. A branch needing a future global statistic
cannot participate as a bounded synchronous consumer. PM04/05/11/41.

## ST05 Distant consumers and memory-aware CSE

```text
recipe = capture exact source versions, arithmetic, invariants and validation position
for each legal alternative:
    materialize once: retain full backing through last use
    synchronized share: retain one bounded block through all borrowers
    replay: rerun only a pure/total recipe over unchanged captures
    spool: write exact values once; replay checked values at each consumer
estimate each alternative's allocation intervals, compute and storage traffic
select within the declared budget; report cost model as unmeasured until calibrated
every provider read/replay completes whole-read validation at its admitted boundary
```

CSE's equality test does not alone prove replay legality or a good lifetime choice.
This is a storage policy sketch, not an automatic instruction to undo all CSE. PM01/02/05/59.

## ST06 Selection with no selected array demand

```text
accumulators = original seeds
for each logical source value, in order:
    p = predicate(value)
    if p:
        mapped = selected_map(value)
        advance count/sum/sumsq using their original operations
finish_read_at_original_boundary(source), when provider-backed
return required scalar results
```

No mask/positions/value pool is needed only after complete demand census. Preserve the
unseeded empty-fold failure at its original position; the first pilot uses explicit seeds.
Predicates and selected maps must admit the new evaluation schedule. PM17/18/19/56.

## ST07 Structural group to row/outer fold

```text
outer = outer_seed
for each declared segment in source segment order:
    row = row_seed
    for each bounded window of this segment, in row order:
        row = fold_in_order(row, row_map(window))
    outer = outer_step(outer, row)
finish_read_at_original_boundary(source), when provider-backed
return outer
```

This removes the full grouped payload, not the input's existing segment metadata. A row
kernel requiring the whole group is a barrier: one huge group can require replay/spool.
Original empty-group and outer-seed semantics remain. All moved kernels total. PM10/15/57.

## ST08 Dynamic groups beyond the memory budget

```text
bounded-domain arm, only if all declared states fit:
    positional numeric keys: drop negative rows; reject keys >= G at group_keys' site
    EnumIdx keys: map to declared position; accept declared negative members
                 reject unknown members, including undeclared negative values
    retain one original state per declared key, including required empty groups
    advance each chain in its source row order; emit results in declared key order

dynamic external arm, initially fixed-width Int64 keys/scalar states:
    require pure/total seed and kernel independent of first-seen GROUP ORDINAL
    require no other-group result dependency and no grouped-row/index observer
    for each input row in source order:
        keep its original ordinal i even when earlier rows were dropped
        if key < 0: drop this numeric row, as the dynamic group_keys regime does
        else append (key, i, needed_columns) to the checked raw spool
    finish each provider read at its original boundary; seal raw spool
    keyed = ST14(raw, ordering = (key, original_row_ordinal))
    while keyed has another key k:
        first_seen = keyed.peek().original_row_ordinal  // first/minimum i for k
        state = original seed for k, once; or absent for an unseeded fold
        while keyed has a record and keyed.peek().key == k:
            row = keyed.next_checked()
            advance state using that row and the original scalar step
        append (first_seen, k, finished_state) to the checked group-result spool
    finish keyed reader checks; seal group-result spool
    ordered = ST14(group-result spool, ordering = first_seen)
    stream ordered records to the downstream consumer in first-occurrence group order
    finish ordered reader checks before publishing any terminal
```

Peeking never dereferences an exhausted reader. This initial external route uses
sorting directly, so one huge key group needs one scalar
state, not a resident hash partition or recursive repartition fallback. No partial-sum
merger changes its strict chain. Every discovered group is nonempty; zero discovered
groups evaluate zero per-group seeds. The bounded-domain arm retains PM20's exact empty
group and seed behavior and original key-validation order.

Sorting results later cannot repair a seed/kernel that used the wrong group ordinal.
Such a dynamic case declines until an explicit first-seen-rank assignment and join-back
stage exists. String/tuple regimes require their own exact equality/drop contracts;
the numeric negative sentinel is not an EnumIdx or general join rule. Source rows,
sorted generations, group-result records and disk manifests are quota-charged. ST14
bounds RAM metadata and handles; resident group results still cost O(G). Reuse W3-1/PM20/21.

## ST09 Statistics barrier with exact replay

```text
if all needed values fit row budget: retain row, compute original statistics, transform
else if a pure total replay recipe with immutable captures exists: replay exact recipe
else if checked scratch policy allows: spool values while computing original statistics
else: retain original materialization if budget allows, otherwise refuse
finish each provider read at its original boundary before the statistic is exposed
second pass: transform each original value with completed statistics; consume in order
finish replay/spool reader checks before publishing the terminal
```

R07 spells out the sequential spool. This preserves the original two-pass arithmetic;
online normalization is a separately named algorithm. New disk/resource faults are part
of the declared resource policy. PM12/13/46.

## ST10 Multi-stage spatial halos

```text
for each demanded final-output interior tile:
    final_demand = interior + final stencil's exact halo/boundary rule
    intermediate_demand = propagate final_demand through intervening map
    input_demand = propagate intermediate_demand through first stencil
    borrow input windows under a bounded cache
    produce only needed intermediate cells, retaining overlaps needed by next tile
    compute final interior exactly once; store or fold it in the required order
    release input/intermediate regions whose last demand has passed
complete whole-read validation at each original provider boundary before publication
```

Use origin-aware windows, not full-axis pointer tables. Halo composition includes boundary
mapping; merely adding radii can be insufficient for nontrivial boundary rules. Overlap
recomputation must be total and preserve each cell's arithmetic. Temporal recurrence variants
wait for PM33/N01 windowing; pure spatial chains do not. PM36/37/42.

## ST11 Files, projection and segmented terminals

```text
require provider capability + immutable snapshot + legal skipped-validation behavior
walk existing file/segment descriptors with a bounded cursor/cache
for each requested window:
    decode only physically supported projected fields/chunks
    perform all required validations, including excluded-data checks when necessary
    advance group/terminal state in original logical order
finish every whole-file/read validation at its original boundary, in source order
publish only after all required reads and validations finish
```

Projection can save zero physical bytes if the codec/chunk stores fields together. A
provider that must decode excluded fields for validation still does so and accounts for
them. Do not mistake a resident list of all file descriptors for bounded metadata. PM15/41/44/48.

## ST12 Layout barrier

```text
first try: translate consumer coordinates directly into the immutable source layout
next try: pack one bounded tile in consumer order, preserving each fold's source order
last external arm:
    write canonical bounded tiles into a private indexed spool
    read tiles/windows in the consumer's required traversal
    retain no full transpose unless the result itself is demanded
finish provider reads at original boundaries and checked-spool reads before publication
```

Reordering iteration for locality is not automatically legal for a fold. A copied transpose
can observe an earlier source version than a delayed view; mutation blocks that substitution.
Random-window spool indexing requires a separate bounded catalog design, beyond R07. PM25–27/31.

## ST13 Fixed-order tensor mode products

```text
for each demanded final slab:
    derive the exact earlier-stage slabs needed by the fixed contraction chain
    choose retain / total replay / spool for shared earlier-stage slabs
    for each stage output cell:
        acc = that cell's original seed
        traverse that stage's contraction index in original order
        acc = original_scalar_step(acc, operands)
    deliver the final slab to its required resident destination or terminal
complete provider whole-read checks at original boundaries before exposing the result
```

Do not change `((X times A) times B) times C` into another contraction order to fit the
budget. A slab can depend on an entire earlier axis; deriving demand may reject fusion.
The required final tensor is counted even when T1/T2 become bounded slabs. PM31/N09 owner.

## ST14 Stable external sort

```text
require fixed-width records, pure/total extraction and a pinned total ordering
compare by requested ordering key, then source ordinal for stable ties
choose bounded run capacity K and merge fan-in F from the actual byte/handle budget
require K >= 1 and F >= 2 for any multi-run problem; otherwise refuse before work
reserve K records + bounded in-place heapsort workspace + writer/catalog buffers
current_manifest = new private checked manifest; run_count = 0
while input has records:
    fill at most K records, preserving their original ordinals
    heapsort this bounded buffer using the exact comparator
    write/check/close a private run, recording its fixed-width ID, count and byte length
    append its fixed-width descriptor to current_manifest; checked-increment run_count
finish original provider-read validation, if present; seal/close current_manifest
release K-run scratch and its writer buffers; keep any required source backing live

while run_count > 1:
    next_manifest = new private checked manifest; next_count = 0
    open bounded cursor over the complete current_manifest
    while that cursor has descriptors:
        take the next min(F, remaining_count) descriptors into a fixed F-slot array
        reserve all batch read blocks + one write block + F heap entries + descriptors
        open checked readers; load at most one head record per nonempty reader
        heap = those heads ordered by the same full comparator
        while heap is not empty:
            head = pop minimum; append head.record to the bounded output writer
            advance only head.reader, refilling its read block when exhausted
            if that reader has a next record: push its next head into heap
        finish every reader's count/footer/integrity checks
        seal/check/close the merged output run
        append its descriptor to next_manifest; checked-increment next_count
        close batch handles and release batch memory; continue with the NEXT batch
    finish current_manifest reader; seal/check/close next_manifest
    require next_count == ceil_div(run_count, F) and next_count < run_count
    // Ownership catalog already names both generations; no old run retired earlier.
    walk current_manifest with a bounded cursor and remove its old owned runs
    release disk credit only after each successful deletion; retain failures for cleanup
    remove old manifest when safe; current_manifest = next_manifest; run_count = next_count

if run_count == 0:
    check the empty manifest; return an empty checked-run lease
else:
    transfer the sole run and manifest, with their reservations, into a checked-run lease
    clean only intermediate owned scratch; return that lease to the consuming stage
```

`ST14(...)` returns this owning lease, not a reader into already deleted files. Its
consumer streams the final run into projection/group/terminal work or a required output
array, completes the final run/manifest checks, then closes and removes the leased files.
Publication and original empty-terminal errors occur only at their admitted boundaries.
ST08 and ST15 keep leases alive through their last read; no final file disappears early.

The ownership catalog is itself an append-only checked disk record stream, traversed with
bounded cursors during cleanup; it is not a vector of every run/handle. Record creation,
manifest/catalog growth and both old/new generations reserve disk bytes before writing.
Run paths derive from the private root and bounded generation/run IDs; fixed-width
descriptors do not contain an unbounded collection of path strings.
Generation retirement starts only after every replacement run and its manifest is checked
and closed. Interrupted work remains owned for cleanup; this sketch promises no resume
protocol. Deletion failure preserves its charge and ownership record and follows the
resource policy, rather than pretending space became available.

RAM is bounded by the chosen K-run phase or F-merge phase plus fixed manifests/metadata,
not the total run count. Checked integer counters and arithmetic descriptor offsets avoid
per-run RAM indices. Peak disk includes other live spools, both generations, catalogs and
cleanup allowance. A required output array is additional live storage. Ties retain original
order. NaNs/unsupported keys decline until their semantics are pinned; ordinals do not fix
a bad comparator. Huge variable records are outside v1. PM16/21; no top-k substitution.

## ST15 External join to a strict aggregate

```text
require Int64 keys, fixed-width payloads, exact equality, pure/total moved computations
ingest each side at its original source position, preserving original row ordinals
finish that side's whole-read validation before crossing its original read boundary
L = ST14(left records, ordering = (key, left_ordinal))
R = ST14(right records, ordering = (key, right_ordinal))
pairs = new checked fixed-record spool; J = 0
while both sorted readers have a record:
    if L.key < R.key: advance L by one checked record; continue
    if R.key < L.key: advance R by one checked record; continue
    k = L.key
    right_group = new checked spool for this one key
    while R has a record and R.key == k:
        append R.next_checked() to right_group
    seal right_group with its exact count
    while L has a record and L.key == k:
        left_row = L.next_checked()                 // only ONE left row retained
        replay right_group from its beginning with one bounded checked read block
        for every right_row in that replay, in right ordinal order:
            next_J = checked_add(J, 1)
            reserve pair-record disk growth before writing; refuse at quota boundary
            append (left_row.ordinal, right_row.ordinal,
                    original_pair_payload(left_row, right_row)) to pairs
            J = next_J
        finish the replay's count/footer/integrity checks; close reader
    remove right_group; release its credit only on successful deletion
drain/check remaining sorted records and both reader footers; seal pairs with count J
retire sorted sides when no remaining observer requires them, keeping failed deletions charged
ordered_pairs = ST14(pairs, ordering = (left_ordinal, right_ordinal))
state = original terminal seed at its admitted point
for each record in ordered_pairs:
    evaluate the original predicate once
    if selected: advance the original scalar chain using the original pair payload
finish ordered-pair reader validation, apply original empty-fold rule, then publish
```

This is a concrete skew-safe fallback: even one equal-key group larger than memory uses
one left row plus a bounded replay block. Replaying that right group can be expensive;
report its full read amplification. Negative keys participate in equality normally; the
group_keys negative sentinel does not apply. Each matching `(i,j)` occurs exactly once;
sorting those pairs restores left-major/right-minor order before predicate/fold evaluation.
No `sum(left)*sum(right)` distributivity changes the strict graph.

Disk admission covers live sorted sides, the current right-group spool, pair records,
sort generations, manifests and cleanup allowance; later pair-sort old/new overlap still
counts after the sides are retired. J may reach `N_left*N_right`, so checked count/growth
reservations enforce the quota without assuming selectivity or storing a J-sized RAM
index. Quota failure yields no terminal and follows the declared resource cleanup path.
All required input/output observers stay live; a resident match output adds its `bJ`
allocation. W3-2/PM22–24 retain ownership of the corresponding join work.

## ST16 Produced chunks to a public sink

```text
require provider-specific private-stage/commit/reconcile capabilities and resource policy
all precommit quota/encoding/I/O failures route to ABORT_RESOURCE, including seal/begin
raw = new checked exact-bit private spool; nonfinite_count = 0; first_bad_flat = absent
for each output block in declared logical order:
    produce every required cell, including cells after an observed nonfinite value
    for each produced storage cell (flat, value), exactly once:
        if the original writer's nonfinite predicate rejects value:
            checked-increment nonfinite_count
            first_bad_flat = min-present(first_bad_flat, flat)
    append exact bits to raw; NaN/Inf/signed-zero payloads are valid PRIVATE spool data
    on producer/read semantic failure: latch that original failure and go to ABORT_SEMANTIC
    on spool/quota/I/O failure: go to ABORT_RESOURCE under the stated new resource policy
finish all producer work and each whole provider-read validation at its original boundary
on any such semantic failure: latch it and go to ABORT_SEMANTIC
seal/check raw with exact shape/cardinality
if nonfinite_count != 0:
    latch the original writer diagnostic with FULL count and FIRST FLAT STORAGE cell
    go to ABORT_SEMANTIC

provider_private = begin_private()             // only after original validation passes
replay raw into bounded provider encoding/write buffers; stage checked private chunks
finish raw reader count/footer/integrity checks; finalize exact shape/index metadata
validate_complete(provider_private)             // no unvalidated cells remain
on any precommit resource/I/O failure: go to ABORT_RESOURCE
outcome = commit(provider_private)
if outcome == ConfirmedCommitted:
    record publication; clean only remaining owned scratch; NEVER destructive-abort output
    report committed result plus any separate cleanup failure
    return confirmed publication outcome
else if outcome == ConfirmedNotCommitted(error):
    latch error; go to ABORT_RESOURCE
else if outcome == Uncertain(token):
    retain ownership manifest, staging/evidence and their charges for reconciliation
    report uncertain publication; do not blindly retry commit, abort or delete target
    return unresolved publication outcome

ABORT_SEMANTIC:
    stop work; close handles; abort only uncommitted private state; remove owned scratch
    preserve cleanup-failure records/charges; then report the latched semantic failure
    return semantic failure
ABORT_RESOURCE:
    stop work; close handles; abort only confirmed-uncommitted private state
    clean owned scratch, preserving cleanup failures; report the resource-policy error
    return resource failure
```

Current writer behavior produces the array, then completes its nonfinite scan, then writes.
The latched tally does not fail early: a later producer/read failure still wins. Use the
writer's actual cell predicate (including complex components where supported), checked
count and physical flat-cell numbering. A non-flat production order takes the minimum
storage ordinal, not the first arrival; it needs a proved bijection to stored cells or
declines. Initial dense fixed-width output makes that mapping explicit and arithmetic.

Raw exact-bit staging avoids asking a provider codec to accept invalid data before the
original writer validation. It can introduce resource/I/O faults; those are explicitly
new policy outcomes, not claims of baseline diagnostic equivalence. Full raw spool,
provider-private generations, encoding buffers, catalogs and cleanup all remain charged.
Unknown selected cardinality is finalized before publication. Resident-output contracts
retain their array too. Uncertain commit requires provider-specific reconciliation using
the saved identity/token. An exception after commit submission is uncertain unless the
provider proves it did not publish; no generic rename or delete-target recovery is promised.
PM29/30/43/45.

## ST17 Recurrence states, snapshots and AD tapes

```text
classify trailing-only / fixed snapshots / complete history / AD demand
if trailing-only or admitted snapshots: use PM33/34 ring + distinct snapshot destinations
if AD: use the prescribed reverse rule, with a separately chosen tape/checkpoint policy
stage exact primal blocks where replay is unavailable or too costly
reverse reads blocks in actual reverse-use order, preserving each cotangent update graph
provider reads finish at original boundaries; checked tape readers finish before publication
```

Ring classification does not automatically admit AD or guards. PM33/N01 must land before
bounded recurrence-history variants. Non-recursive primal lifetime/replay is independent.
Spilling changes storage; checkpoint replay adds computation and its own semantic proof.
PM33–40/59 provide the detailed sketches.

## ST18 Addressed RNG to repeated consumers

```text
capture key, stream, distribution parameters and logical sample offset once
validate parameters at their original evaluation point
for each demanded logical sample address:
    generate the same addressed sample (including distribution subdraw mapping)
    apply total consumer recipe; fold/store according to the declared task
later consumers choose total regeneration or checked exact-value spool
```

Global logical addresses, not chunk-local counters, determine samples. Do not replace a
sequenced generator by `_at`; do not bypass unsupported backend/distribution prerequisites.
AD replay uses the same sample identity. PM40/47/48 and N08's backend gate remain shared.
