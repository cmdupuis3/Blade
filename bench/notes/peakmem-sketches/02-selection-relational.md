# Selection and relational implementation sketches

Status: proposed design sketches, 2026-10-21. **Uncompiled and unexecuted.** These are
generated-C++20/F#-shaped algorithms, not new Blade syntax or production runtime APIs.
Coverage is exactly PM17–PM24 and PM55–PM58 from the
[parent plan](../peakmem-microkernel-plan.md); its
[review dispositions](../peakmem-microkernel-review-claude.md) remain controlling.
W2-1/W2-2/W3-1/W3-2 retain their existing speed-work owners. An algorithm below does
not assert that its admission, IR node, interpreter twin, or lowering already exists.

### Shared contracts and notation

`N` is input rows, `m` selected rows, `G` groups, `J` matched pairs, `q` result columns,
`B` staging cells, and `b` bytes per payload cell. Inputs, required outputs, metadata,
allocator capacity and overlapping old/new allocations all remain in the memory ledger.
No output is replaced with a checksum. A stored result stays live through its required
consumers. All initial sketches use resident, immutable scalar arrays whose shape and
provider checks have completed; producer fusion needs a separate proof of totality and
evaluation/failure order. Group-key and terminal checks remain at their source sites.
Span arguments borrow storage. Payload-only returns below keep their source-language
result type and index descriptor at the call site; demanded key mappings are not erased.

`Exact<T>` is a proposed owning scalar buffer: `exact<T>(n)` checks `n*sizeof(T)` before
allocation, exposes length `n`, and supports indexing/span. It does not imply the system
allocator reserves exactly that many bytes. It must account for actual capacity and
avoid an unnecessary initialization pass. `checked_add`/`checked_mul` reject overflow
before allocation; such resource refusals are reported separately from language errors.
`original_empty_failure(site)` invokes the original BL8003 diagnostic with its location.

```cpp
struct StrictFold {                         // scalar sketch; no reassociation
    std::optional<double> value;             // exact seed, or absent for unseeded
    double (*step)(double, double);
    void push(double x) {
        if (!value) value = x;               // first item is not combined twice
        else value = step(*value, x);        // preserve this parenthesized chain
    }
    double finish(SourceSite site) const {
        if (!value) original_empty_failure(site);
        return *value;                       // seeded empty fold returns its seed
    }
};
```

The compiler supplies the source fold's exact operand order and operation to `step`.
Seeds retain their source evaluation count and exact value; they are not implicitly zero
and are not multiplied by group size. Moving seed evaluation or interleaving independent
chains requires pure, total kernels and seeds. A may-fail expression is insufficient for that
admission even when it is pure. Source-order diagnostics are retained or the rewrite
declines. Parallel merging and algebraically different aggregate formulas are separate
licensed/algorithm arms, never hidden inside these serial sketches.

## PM17

**Stable materialized selection. Existing:** counted compaction and positions elision
exist; the gather-at-mask route can reserve `N+1` cells per buffer
(`src/CodeGenExpr.fs:2150`, `:2300`; `src/CodeGenBinding.fs:2541`). **Proposed:** compare
exact output sizing and staging under the same output and original-axis mapping.

```cpp
// keep was evaluated ONCE at the original mask site, in source order.
auto select_exact(std::span<const double> x, std::span<const uint8_t> keep) {
    size_t m = 0;
    for (size_t i = 0; i != x.size(); ++i) m += (keep[i] != 0); // m <= N
    auto out = exact<double>(m);
    size_t r = 0;
    for (size_t i = 0; i != x.size(); ++i)
        if (keep[i]) out[r++] = x[i];
    return out;                             // r == m; retained owning result
}
// One predicate call per input; blocks are RETAINED until copied to final output.
auto select_staged(std::span<const double> x, size_t B, auto predicate) {
    require(B > 0);
    std::vector<Exact<double>> blocks;
    size_t m = 0;
    for (double v : x) if (predicate(v)) {   // pure, total; N calls, original order
        if (m % B == 0) blocks.push_back(exact<double>(B));
        blocks.back()[m % B] = v;
        ++m;
    }
    auto out = exact<double>(m);
    for (size_t r = 0; r != m; ++r) out[r] = blocks[r / B][r % B];
    return out;                             // destroy blocks only after copying
}
```

Exact sizing retains the existing mask plus `bm` output; demanded keyed reads also need
their source-ordinal mapping. Staging retains `b*B*ceil(m/B)` plus `bm` during assembly,
including block descriptors: bounded block size is **not** bounded total workspace.
A count/replay variant may omit the mask only when the predicate is repeatable, pure,
total, and permitted to be evaluated twice; record `2N` predicate calls, versus `N` above.
Controls: empty/all-selected, expensive predicate, random/runs, escaping mask and keyed
result. Decline fusion across a failing producer/predicate or mutable source snapshot.

## PM18

**Shared selection across columns. Existing prerequisite:** W2-2 owns shared selection;
do not assume its general lowering is present. **Proposed:** one immutable selection
descriptor shared by all selected outputs over the same source axis identity.

```cpp
struct Selection { AxisIdentity source_axis; Exact<size_t> source_ordinal; };
auto make_selection(AxisIdentity axis, std::span<const uint8_t> keep) {
    size_t m = 0;
    for (uint8_t k : keep) m += (k != 0);
    auto pos = exact<size_t>(m);
    size_t r = 0;
    for (size_t i = 0; i != keep.size(); ++i) if (keep[i]) pos[r++] = i;
    return std::make_shared<const Selection>(Selection{axis, std::move(pos)});
}
auto project_columns(auto const& columns, auto shared_selection) {
    std::vector<SelectedColumn> outputs;
    for (auto const& col : columns) {        // source column demand order
        require_same_axis(col.axis, shared_selection->source_axis);
        auto out = exact<double>(shared_selection->source_ordinal.size());
        for (size_t r = 0; r != out.size(); ++r)
            out[r] = col[shared_selection->source_ordinal[r]];
        outputs.push_back({std::move(out), shared_selection});
    }
    return outputs;                         // retain every requested column
}
```

Target: `q*bm` payload plus one `sizeof(size_t)*m` positions table, counted once while any
owner lives. Equal extents alone do not establish alignment. A row-major fused fill is
an optional pure/total variant; it changes inter-column evaluation order. If no consumer
observes original keys, a mask scan can fill columns without storing positions; the
logical axis descriptor still survives. Controls include one keyed output keeping shared
metadata alive, differing axis tags, mutation, and requested nullable-schema checks.

## PM19

**Selection to statistics. Proposed:** the selection and mask have no other observers.
The first arm returns explicitly seeded count/sum/sumsq, with the same multiplications
and separate sum chains as the materialized reference; it is not Welford's algorithm.

```cpp
struct Stats { size_t count; double sum; double sumsq; };
Stats selected_stats(std::span<const double> x, auto predicate,
                     double sum_seed, double sumsq_seed) {
    Stats s{0, sum_seed, sumsq_seed};         // seeds already evaluated exactly once
    for (double v : x) {                    // original encounter order
        const bool selected = predicate(v);// N evaluations, including rejected rows
        if (!selected) continue;
        ++s.count;                          // <= N; no increment for rejected rows
        s.sum = s.sum + v;
        const double square = v * v;        // same FP operation as reference
        s.sumsq = s.sumsq + square;
    }
    return s;
}
// Unseeded variant: maintain separate StrictFold states, not zero initializers.
// After the full admitted scan, call finish at the source terminals, in source order.
double selected_fold(std::span<const double> x, auto predicate,
                     StrictFold fold, SourceSite site) {
    for (double v : x) if (predicate(v)) fold.push(v);
    return fold.finish(site);
}
```

Target: bounded state, no Bool mask, positions, selected values, or square array. Initial
admission requires predicate, selected scalar map, and fold steps to be pure and total:
the reference evaluates the complete mask before a later reduction. Preserve the empty
unseeded BL8003 at its original terminal and seed behavior for empty inputs. A second mask
consumer, printed selection, failing later predicate, or failing earlier fold blocks this
schedule. An expensive-predicate control must still report exactly `N` predicate calls.

## PM20

**Bounded-key group aggregates. Existing:** `genGroupKeysBinding` builds stable CSR for
positional and EnumIdx groups (`src/CodeGenBinding.fs:859`, `:962`, `:993`). Aggregate-only
lowering belongs to W3-1. **Proposed:** scan stored inputs into `G` scalar fold states.

```cpp
// A declared-key map returns ENUM POSITION, never numeric sorting of key values.
std::optional<size_t> bucket(int64_t k, KeyRegime regime, size_t G,
                            auto const& declared_to_slot, SourceSite site) {
    if (regime == KeyRegime::Enum) {         // negative declared members are valid
        auto it = declared_to_slot.find(k);
        if (it == declared_to_slot.end()) original_group_key_failure(site); // BL8006
        return it->second;
    }
    if (k < 0) return std::nullopt;          // positional numeric negative drops row
    if (uint64_t(k) >= G) original_group_key_failure(site);
    return size_t(k);
}
auto bounded_groups(auto const& keys, auto const& values, size_t G, auto key_to_slot,
                    auto make_fold, SourceSite fold_site) {
    for (auto k : keys) (void)key_to_slot(k);// validate at original group_keys site
    std::vector<StrictFold> state;
    for (size_t g = 0; g != G; ++g) state.push_back(make_fold(g));
    for (size_t i = 0; i != keys.size(); ++i)
        if (auto g = key_to_slot(keys[i])) state[*g].push(values[i]);
    auto result = exact<double>(G);
    for (size_t g = 0; g != G; ++g) result[g] = state[g].finish(fold_site);
    return result;                          // declared outer index/order retained
}
```

Inputs have equal proven source axes. `make_fold(g)` evaluates the original per-group
seed once, including empty groups; it may be absent. Initial fusion requires pure, total
seed/step bodies because the original group-major evaluation becomes row-interleaved.
An unseeded empty group still fails in declared group order; never silently omit it.
Target: `O(G*q)` states plus required result/key metadata, without permutation or grouped
payload. Controls: G=0, unknown keys, negative Enum members, positional drops, nonzero
seeds, empty groups, grouped-output observer and per-group captured counter.

## PM21

**Dynamic group aggregates. Existing:** dynamic discovery is first-occurrence ordered,
with negative numeric rows dropped (`src/CodeGenBinding.fs:922`). **Proposed:** preserve
that discovery phase, then maintain sequential per-group chains without CSR payload.

```cpp
auto dynamic_groups(std::span<const int64_t> keys, auto const& values, auto make_fold,
                    SourceSite fold_site) {
    std::unordered_map<int64_t, size_t> slot;
    std::vector<int64_t> first_seen;
    for (int64_t k : keys) {
        if (k < 0) continue;                // this single-numeric-key regime only
        if (slot.find(k) == slot.end()) {
            const size_t g = first_seen.size();
            slot.emplace(k, g);
            first_seen.push_back(k);
        }
    }
    std::vector<StrictFold> state;
    for (size_t g = 0; g != first_seen.size(); ++g) state.push_back(make_fold(g));
    for (size_t i = 0; i != keys.size(); ++i) {
        if (keys[i] < 0) continue;
        state[slot.at(keys[i])].push(values[i]);
    }
    auto result = exact<double>(first_seen.size());
    for (size_t g = 0; g != result.size(); ++g) result[g] = state[g].finish(fold_site);
    return GroupResult{std::move(first_seen), std::move(result)};
}
```

Returning keys uses `first_seen`, never hash-table iteration. Within each group, row `i`
precedes row `j` iff `i<j`; seed and operand order are exactly PM20's. Zero discovered
groups returns an empty outer result and evaluates zero per-group seeds. String/tuple-key
extensions must reuse their actual equality and negative/drop rules, not extrapolate the
single numeric case. Target is `O(G)` hash/key/state capacity; count hash rehash overlap,
vector growth and output lifetime. Do not reserve N buckets and call it `O(G)` memory.
Controls: all distinct, all dropped, skew, collisions, group observers and failing seeds.

## PM22

**Membership working set. Existing:** `IRContains` emits a linear scan and places the
target expression inside its comparisons (`src/CodeGenExpr.fs:572`); W2-1 owns hoisting.
**Proposed:** initial input is an already evaluated stored scalar target and integer set.
General target-evaluation normalization needs a separate semantics/differential decision.

```cpp
uint64_t ordered(int64_t x) { return uint64_t(x) ^ (uint64_t{1} << 63); }
auto bitmap_if_bounded(std::span<const int64_t> set, uint64_t bit_limit) {
    if (set.empty()) return Bitmap::empty();
    uint64_t lo = ordered(set[0]), hi = lo;
    for (int64_t k : set) { lo = std::min(lo, ordered(k)); hi = std::max(hi, ordered(k)); }
    const uint64_t delta = hi - lo;          // unsigned, no signed max-min overflow
    if (delta == UINT64_MAX || delta >= bit_limit) return Bitmap::decline();
    const uint64_t span = delta + 1;
    const uint64_t nbytes = span / 8 + (span % 8 != 0); // no span+7 overflow
    if (nbytes > SIZE_MAX) return Bitmap::decline();
    auto bits = zeroed_exact<uint8_t>(size_t(nbytes)); // initialization is required
    for (int64_t k : set) {
        const uint64_t p = ordered(k) - lo;
        bits[size_t(p / 8)] |= uint8_t(1u << (p % 8));
    }
    return Bitmap::ready(lo, hi, std::move(bits));
}
bool bitmap_contains(Bitmap const& b, int64_t target) {
    if (b.is_empty()) return false;
    const uint64_t k = ordered(target);
    if (k < b.lo || k > b.hi) return false;   // before subtraction/indexing
    const uint64_t p = k - b.lo;
    return (b.bits[size_t(p / 8)] & uint8_t(1u << (p % 8))) != 0;
}
```

Derive `bit_limit` from the admitted bitmap workspace with checked arithmetic. On decline,
choose the declared linear or hash arm, never an unchecked bitmap allocation.
Hash uses exact integer equality, inserts each key, and probes `find`; duplicates do not
change Boolean output. `zeroed_exact` explicitly writes the required zeros. Compare build
plus probes as one task; a reuse study retains the set across its named probes. Controls:
empty set, negative/INT64 extremes, wide sparse span, duplicates, one/many probes, and
target expressions with effects/failures (decline). Floating/nullable equality is separate.

## PM23

**Join to aggregate. Proposed terminal task:** only the specified fold escapes. Reuse
W3-2's stable equijoin owner; `IRJoin` is array concatenation, not an available relational
join primitive. First prototype uses stored integer keys and pure, total scalar payloads.

```cpp
using StableBuckets = std::unordered_map<int64_t, std::vector<size_t>>;
StableBuckets index_right(std::span<const int64_t> right_keys) {
    StableBuckets buckets;
    for (size_t j = 0; j != right_keys.size(); ++j)
        buckets[right_keys[j]].push_back(j);// each bucket is ascending right ordinal
    return buckets;
}
void visit_pairs(auto const& left_keys, StableBuckets const& buckets, auto visit) {
    for (size_t i = 0; i != left_keys.size(); ++i) {
        auto it = buckets.find(left_keys[i]);
        if (it == buckets.end()) continue;
        for (size_t j : it->second) visit(i, j); // left-major, right-minor
    }
}
double join_fold(auto const& left_keys, auto const& right_keys, auto pair_value,
                 StrictFold fold, SourceSite site) {
    auto buckets = index_right(right_keys);
    visit_pairs(left_keys, buckets, [&](size_t i, size_t j) {
        fold.push(pair_value(i, j));        // every matching pair exactly once
    });
    return fold.finish(site);
}
```

Keep the right ordinal index and required inputs; eliminate only the `bJ` joined payload.
Bucket-vector capacity and transient reallocation count toward peak. A stable CSR index
can replace vectors by count/prefix/forward-fill without changing `visit_pairs` order.
No product-of-sums or multiplicity shortcut: these change the FP graph. Empty sides,
duplicate 2x2 matches and hot-key fanout are controls. Printed matches or a may-fail
pair producer before a may-fail fold decline fusion; the original builds all pairs first.
Group-specific negative-drop rules do not apply to relational key equality.

## PM24

**Materialized join. Proposed/gated on W3-2:** all `J` projected matches remain resident,
with the same pair order as PM23. This arm compares allocation policy, not output demand.

```cpp
auto join_exact(auto const& left_keys, auto const& right_keys, auto pair_value) {
    auto buckets = index_right(right_keys);
    size_t J = 0;
    for (auto key : left_keys) {
        auto it = buckets.find(key);
        if (it != buckets.end()) J = checked_add(J, it->second.size());
    }
    auto matched = exact<Match>(J);          // checked J*sizeof(Match), then allocate
    size_t r = 0;
    visit_pairs(left_keys, buckets, [&](size_t i, size_t j) {
        matched[r++] = pair_value(i, j);    // payload evaluated once, not in count pass
    });
    return matched;                         // r == J; keep through all required uses
}
auto join_growth(auto const& left_keys, auto const& right_keys, auto pair_value) {
    auto buckets = index_right(right_keys);
    std::vector<Match> matched;
    visit_pairs(left_keys, buckets, [&](size_t i, size_t j) {
        matched.push_back(pair_value(i, j));
    });
    return matched;                         // record capacity, including growth peaks
}
```

Exact count/fill trades an extra key probe traversal for no payload growth copies;
the payload callback must be pure/total for the initial common admission. Staging uses
PM17's retained blocks and final copy, so it can overlap approximately two J-sized payloads;
it is not a bounded-memory terminal. A later external join is a composition with the
streaming companion's ledger/spool, not this resident contract. Controls: J=0, huge fanout,
count overflow, worst-arm capacity guard, duplicate order, and required output-store audit.

## PM55

**Fixed-budget capacity. Proposed measurement control:** no new runtime algorithm.
The following F# sketch plans/classifies future observations; it has not run a trial.
Budget names identify metric, coverage, enforcement and reserved headroom explicitly.

```fsharp
type Budget = { Limit: uint64; Headroom: uint64; Metric: string; Coverage: string }
type Status =
    | ValidFits of peak: uint64
    | BudgetExceeded of peak: uint64
    | PredictedSkip | TimedOut | AllocationFailed | Incorrect | MetricUnavailable
type Observation =
    { N: int64; Arm: string; Status: Status; Contract: string; BudgetId: string }

let usable b =                         // planning refusal, not wrapped subtraction
    if b.Headroom > b.Limit then invalidArg "budget" "headroom exceeds limit"
    b.Limit - b.Headroom

let schedule b arm commonOddSizes armUpperBound =
    commonOddSizes |> List.map (fun n ->
        // Upper bound includes inputs, required outputs, metadata, overlap, scratch.
        // None means unbounded/unknown: no unsafe pilot and no claim that n failed.
        match armUpperBound arm n with
        | Some bytes when bytes <= usable b -> arm, n, "eligible"
        | _ -> arm, n, "predicted-skip")

let classifyCompleted b correct peak =
    if not correct then Incorrect
    else match peak with
         | None -> MetricUnavailable
         | Some p when p > usable b -> BudgetExceeded p
         | Some p -> ValidFits p

let capacity contract budgetId arm observations =
    observations
    |> List.filter (fun r -> r.Contract = contract && r.BudgetId = budgetId && r.Arm = arm)
    |> List.choose (fun r -> match r.Status with ValidFits p -> Some (r.N, p) | _ -> None)
    |> List.sortByDescending fst
    |> List.tryHead                    // None means no validated fit, not capacity zero
```

The runner must additionally retain measured-budget excess, timeout, allocation failure,
incorrect output and unavailable metric as distinct statuses; process working set is
not tracked allocation capacity. Hard enforcement needs the companion's explicit
resource policy, not a polling loop presented as a hard bound. Headroom is unavailable
to the measured task, so its fit threshold is `Limit-Headroom`. Preflight is per arm on
one common candidate grid: a predicted skip for one arm must not prevent a safe trial
of another arm at that N. Each size uses the same task, retention endpoint, limit and
stopping rule across arms. Mapping from a
validated record to capacity is only `max{N tested and correct and within budget}`;
neither a skipped N nor a failed run proves an upper bound. Predeclare odd size/shape
sequences and stop rules. Non-monotone dispatch/shape effects forbid assuming binary
search monotonicity. Report largest validated tested N, not an inferred infinite ratio.

## PM56

**Mask representation. Proposed internal alternatives:** byte mask, packed bits,
ascending positions, or sorted disjoint nonempty half-open runs. Original axis identity
and logical cardinality are independent of encoding; required external mask output stays.

```cpp
// All conversions consume the SAME stored mask; they do not reevaluate its predicate.
auto encode_mask(std::span<const uint8_t> keep) {
    Encodings e;
    e.bits = zeroed_exact<uint8_t>(keep.size()/8 + (keep.size()%8 != 0));
    size_t i = 0;
    while (i != keep.size()) {
        if (!keep[i]) { ++i; continue; }
        const size_t begin = i;
        do {
            e.bits[i/8] |= uint8_t(1u << (i%8));
            e.positions.push_back(i);
            ++i;
        } while (i != keep.size() && keep[i]);
        e.runs.push_back(Run{begin, i});
    }
    e.cardinality = e.positions.size();
    return e;                              // comparison fixture; production chooses ONE
}
void enumerate_runs(auto const& runs, auto emit) {
    for (Run r : runs) for (size_t i = r.begin; i != r.end; ++i) emit(i);
}
size_t rank_positions(auto const& positions, size_t i) {
    return size_t(std::lower_bound(positions.begin(), positions.end(), i) - positions.begin());
}
size_t select_positions(auto const& positions, size_t r) { return positions.at(r); }
```

Proof obligation: let `K={i | 0<=i<N and keep[i]}` in ascending order. Every encoder
enumerates exactly K once; `m=|K|=popcount(bits)=positions.size=sum(run.end-run.begin)`.
`rank(i)=|K intersect [0,i)|`; `select(r)=K[r]` for `0<=r<m`; thus
`rank(select(r))=r` and `select(rank(i))=i` only when `i` is kept. Keyed membership is
`i in K`, not whether `rank(i)<m`. Position lookup uses lower_bound equality; run lookup
finds the last run beginning at/before i and tests `i<end`; bit lookup tests its bit after
`i<N`. Bit tails remain zero, and positions/runs retain original source ordinals.

Storage models: N byte cells, `ceil(N/8)` bit bytes, `m*sizeof(size_t)` position payload,
or `2*R*sizeof(size_t)` payload for R nonempty runs, plus actual capacity/metadata. The comparison fixture
above holds all encodings only to illustrate equivalence; no production-memory claim may
count it as one representation. Charge conversion overlap and rank/select index tables.
Controls: empty/full, alternating bits, long runs, random keyed probes, and m/N extremes.

## PM57

**Structural group map ungroup. Existing:** group_by can allocate a full grouped pool;
ungroup allocates output and scatters via `gk__at` (`src/CodeGenBinding.fs:2902`, `:3218`).
**Proposed initial admission:** contiguous, complete structural segments, immutable
source, same-length pure/total group mapper, only the final ungrouped result demanded.

```cpp
auto map_segments_direct(ArrayView<double> source, auto const& offsets, auto row_kernel) {
    // Proof before emission: offsets[0]=0, offsets.back()=N, nondecreasing.
    // Every segment's original gk__at(offsets[g]+k) equals offsets[g]+k.
    auto output = exact<double>(source.size());
    for (size_t g = 0; g + 1 < offsets.size(); ++g) {
        const size_t begin = offsets[g], length = offsets[g+1] - begin;
        auto in = source.cells.subspan(begin, length);       // private borrowed view
        auto out = output.span().subspan(begin, length);     // disjoint final region
        row_kernel(in, out);                  // bounded workspace; fills exactly length
    }
    return ArrayResult{source.axis_identity, std::move(output)};
}
// Example admitted group kernel: same two-pass row-centering arithmetic as its source.
void center_row(auto in, auto out, double already_evaluated_seed) {
    double sum = already_evaluated_seed;
    for (double x : in) sum = sum + x;
    const double mean = sum / double(in.size()); // admit only source-defined empty rule
    for (size_t k = 0; k != in.size(); ++k) out[k] = in[k] - mean;
}
```

Mapping proof: `(g,k) -> offsets[g]+k` is a bijection from segment members to `[0,N)`;
using that same destination recovers the original axis, not a fresh joined axis. Target
is final `bN` plus maximum active group workspace and existing segment metadata, without
group-copy/ungroup-copy pools. `row_kernel` is destination passing, not an array-returning
call with a hidden full row allocation. Empty-row arithmetic must match source exactly.
Scattered groups need their original permutation; rectangular tiles need a coordinate
mapping, so neither uses this contiguous proof. Source mutation after original grouping,
escaping group views, aliases or multiple observers decline; a view cannot replace a
snapshot copy across a write. Required final output remains materialized and retained.

## PM58

**Sparse wildcard terminal. Existing:** partial SparseIdx reads gather residual data;
parent sparse keys remain in supplied order, never sorted
(`src/CodeGenExpr.fs:1041`; `src/cpp/index_types.h:510`). **Proposed:** scalar fold only,
scalar payload per parent entry, no escaping residual array or residual keyed lookup.

```cpp
double sparse_partial_fold(auto const& parent, auto const& pinned,
                           StrictFold fold, SourceSite site) {
    // pinned expressions/coordinates validated once at their original read site.
    // Parent rank_to_tuple and values are immutable throughout this terminal region.
    for (size_t r = 0; r != parent.cardinality; ++r) {
        auto const& key = parent.rank_to_tuple[r];
        bool matches = true;
        for (auto const& pin : pinned) {
            if (key[pin.position] != pin.coordinate) { matches = false; break; }
        }
        if (matches) fold.push(parent.values[r]); // original gather order, no sorting
    }
    return fold.finish(site);                // original empty-seed/BL8003 behavior
}
```

Let matching parent ordinals be `r0<r1<...<r(m-1)`. The existing gather's payload order
is those ordinals, so the terminal feeds the identical ordered scalar sequence and
uses the identical seed. Residual keys/rank vectors/value copies are unnecessary only
because no observer requests them. Distinct validated parent keys with the same pinned
coordinates project to distinct residual keys, so this also preserves residual uniqueness.
Target: existing parent storage plus bounded fold
state, not a claim that parent metadata vanishes. Controls: no/all matches, unsorted
insertion keys, multiple pinned/free positions, repeated partial reads and empty fold.
Trailing payload dimensions, parent mutation, residual index demand, escaping mapped
sparse output or failure-order ambiguity decline this initial route. External grouping,
sorting and spilling belong to the streaming companion's later composition contracts.
