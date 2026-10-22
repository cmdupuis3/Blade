# D03 / PM42: adjacent-chunk halo reuse census and narrow pilot design

State: source/design census complete; **reuse implementation not yet admitted**.
Useful repeated reads are evidenced, but immutable-source provenance and bounded
window-addressing/cursor prerequisites remain. No production/test implementation,
build, emission, compiler execution, fixture execution, benchmark or commit.
Lane base: `ec75ae383fab4ae13b34e21eae605d30f48e216c` (accepted D02).
Graft `halo` query found no F# coverage; targeted source reads supplied this census.

## Existing source-reachable idiom

`tests/ZarrTests.fs:1547-1554` already exercises:

```blade
import zarr as z
let sample = z.load("tests/fixtures/zarr_stores/zarr_segments")
type CX = Chunked<sample.index.x, store>
let A: Array<Float like CX> = sample.vars.A |> z.stream
let d = method_for(halo<CX, [-1, 0, 1]>) <@>
    lambda(w) -> A(w(1)) - A(w(-1)) |> compute
```

The store has N=10 values 1..10 and physical chunk edge C=4. The test also
computes a radius-two `d2`, but a first pilot should contain **only d** and one
consumer. Expected d is eight 2.0 values. No user-facing manual tile API is needed.

Existing emitted artifact `generated_cpp_tests/zarr_segments_stencil.cpp` was
read, not regenerated. Its SHA256 is
`B01C2E28C3EDE2809A6B88C23D4AD810DBCBA246551988E6A3BCFD2208982F7F`.
Lines 222-255 show run offsets, window allocation, per-window chunk reads and
validation. The artifact uses v3-style `c/<chunk>` keys. It proves the same
ordinary C++ path named in source despite its location in CodeGenCuda.fs.

| Run core | Valid centers | Requested input window | Physical chunks read |
|---|---|---|---|
| [0,4) | [1,4) | [0,5) | 0,1 |
| [4,8) | [4,8) | [3,9) | 0,1,2 |
| [8,10) | [8,9) | [7,10) | 1,2 |

The current loop reads seven full padded chunks (7*4*8 = 224 disk payload
bytes for Float64). A certified immutable source could serve those requests
with three physical reads (96 bytes), retaining neighboring chunks. These
are source-derived request counts, **not measured I/O or a speed claim**.

## Current emission and ownership anchors

- `src/Types.fs:451-477`: `haloAccessOf`, `haloReach`, `haloDemand`. Reach
  includes zero; Start=1 and Shrink=2 for [-1,0,1]. Halo centers stay in the
  original domain's interior. Ghosts are real neighboring source cells;
  there is no invented boundary zero/mirror/wrap fill.
- `src/IRAccess.fs:23-69`: `WindowRead` and `windowReadsOf` identify actual
  IRIndex nodes by reference, the source ID/rank/dimension and static offset.
  Unknown offsets are reported as None, rather than silently ignored. This
  is the existing matcher/substitution seam for a narrow scalar pilot.
- `src/CodeGenCuda.fs:3520-3583`: `segmentRun` recognizes a halo over a
  Chunked alias, finds streamed window reads and admits one rank-one source.
  Run boundaries come from SegmentTable. Halo reads of multiple streams are
  already refused with a .read steer.
- `src/CodeGenCuda.fs:4087-4169`: `segLoop` allocates the required whole
  output separately, an emitted full run-offset table, and one converted
  input window of maxWin cells. It calls GenStreamRows for each window and
  runs the ordinary kernel at global center coordinates. Window scratch is
  per output/source and freed after the run loop; d and d2 share no cache.
- `src/providers/ZarrProvider.fs:2106-2197`: `genStreamWindowCore` allocates
  one full padded disk chunk vector per window call, traverses intersecting
  chunk keys in order, rereads each whole chunk, casts/copies intersections
  into the converted window, and validates that window afterward. GenStreamRows
  (`:2234`) delegates to this window path. No decoded-chunk cache exists.
- `src/CodeGenCuda.fs:3528-3532`: the segment route bounds the ordinary nest
  using the slab mechanism and explicitly keeps the carousel off. The existing
  resident halo/carousel and streamed provider windows are distinct storage
  paths, not already a shared-chunk cache.

The current emitted rank-one alias is `Array A = { win - __w_lo, extents }`
(`CodeGenCuda.fs:4135`, emitted artifact :255). Forming a pointer before its
allocation is not a sound general borrowed-window representation. A new pilot
must use proven global-to-local indexing, not bless or extend that alias.

## Critical validation distinction

This halo route's original boundary is **each requested window**, not the
whole source read used by D01 scalar terminals or D02 ordinary .read.
`genStreamWindowCore None` runs `refuseScanCpp` after all chunk reads for the
current window, before its kernel. D01 passes Some wholeReadTally through a
separate adapter. Do not apply D01's tally finalization to the halo route.

Any reuse must preserve the current window's ascending chunk-error order,
then scan exactly the current converted window in global coordinates. A cache
hit is not permission to omit window validation; full chunk padding or cells
outside the window must not be scanned at cache insertion. Keep the original
scalar arithmetic/stores and perform no speculative future-window reads.

Concrete paired diagnostics for N=10/C=4:

- NaN at source index 0 plus a short chunk 2: the first window reads only
  chunks 0/1, then reports BL8015 at [0]. Chunk 2 must not be prefetched early.
  Ordinary whole .read would instead encounter the later short read before
  its final missing tally; that is a different contract, not the halo oracle.
- NaN at index 0 plus a short chunk 1: chunk 1 is in the first window, so its
  short-read error precedes BL8015. The same holds for absent chunk 1 with
  null fill. Cache lookup must not change this order.
- NaN padding outside N in the last padded chunk must never cause failure.
  A finite missing-chunk fill remains a value; an absent null-fill chunk fails
  at its original request point.

## Narrow candidate after prerequisites

Candidate bounds: local identity-codec Zarr v2/v3, one rank-one Float64 stream,
one synchronous scalar halo consumer with literal radius one [-1,0,1], N>=3,
uniform physical/store segmentation C>=2, pure total kernel, no source/borrow
escape, no additional source, backend team, MPI, AD, temporal recurrence,
cross-consumer sharing, arbitrary gathers or computed offsets. Unsupported
forms retain the existing path. Require checked static extents/chunk bytes,
matching logical/physical geometry, and bounded global-to-local arithmetic.

Use three fixed owning disk-type chunk slots keyed by source snapshot identity
and chunk ordinal; copy/cast each requested intersection into the existing
converted window. The original typed chunk fetch can fill a slot directly for
the identity codec, so no additional full decode workspace is necessary beyond
those slots. This is not a new general cache/runtime or asynchronous prefetch.
Absent finite-fill chunks need explicit stable fill entries. The converted
window owns its copied ghost values; no kernel can retain a cache-slot pointer.

After a synchronous window finishes, release all old borrows, evict chunks
outside the next cover BEFORE loading misses, and visit misses in the current
window's original provider order. At most three chunks cover a C-sized core
expanded by radius one. Keeping all old slots while allocating new ones would
incorrectly raise transition capacity; the proof must include evictions/pins.

For an accepted pilot the modeled live payload is mandatory output
`(N-2)*8 + converted_window_capacity (C+2)*8 + at_most_3*C*8`, plus three fixed
entry records, cursor, bounds and file/key bookkeeping. No separate ghosts,
queue, retained decoded list or per-chunk lookup directory. Allocator retention
and I/O internals are outside this payload model. Reuse may INCREASE workspace
relative to the current one-chunk reader while reducing repeated I/O.

The current run table is O(N/C), so replacing only the disk buffer does **not**
make the pipeline bounded beyond its mandatory output. For this uniform case,
derive runs arithmetically from the checked source chunk edge and advance with
`min(C, N-lo)`. Do not emit the run table. Compiler-time SegmentTable metadata
is a separate census term, not secretly a runtime constant bound.

For addressing, use WindowRead.Node substitutions for recognized static reads:
`win[(global_center + offset) - window_lo]`, with proof each access is in the
filled interval. Preserve global coordinates for other diagnostics/values;
decline any opaque callable or window use the matcher cannot completely place.
This avoids shifted pointers without adding a user-visible window/tile API.

## Missing prerequisites / disposition

**No existing provider capability proves immutable local Zarr chunk identity
across windows.** `zarrChunkFetch` opens a fresh ifstream on each request.
ProviderSpec (`src/providers/ProviderRegistry.fs:275-298`) has read hooks,
fingerprints and version stamps, but no runtime immutable snapshot/read lease
or borrowed-window ownership contract. `ZarrProvider.storeFingerprint` (:1281)
hashes all store files for compile-time provenance; VersionStamp (:1292) is
max mtime for memoization. Neither pins the runtime payload for repeated reads.

This is observable: an external writer can replace, truncate or create a
previously absent chunk between adjacent windows. The current route rereads it;
a cache would keep old bytes/fill and may suppress a later failure. Purity and
no compiler-visible writes alone do not prove external source stability.
Size/mtime checks do not establish immutable bytes. A controlled quiescent test
fixture is useful measurement evidence, not language-wide admission evidence.

Therefore do not implement cache reuse under the present generic Zarr stream
hook. Root must establish an actual immutable-source/read-session contract or
an already-canonical stability guarantee before admitting the pilot. The bounded
arithmetic cursor and safe addressing seam also need explicit review. These
are named prerequisites, not a request to build a new general runtime wholesale.

## Independent values, controls and diagnostic request

Future root-approved fixtures should pin:

- Existing N=10 linear values: d = [2,2,2,2,2,2,2,2].
- N=11 values A(j)=(j+1)^2: d = [8,12,16,20,24,28,32,36,40].
- N=10 linear data with missing middle chunk 1 filled by -1:
  d = [2,2,-4,-5,0,0,10,11]. These include cross-chunk ghosts and an odd tail.
- Radius two, nonuniform segments, rank two, multiple streams/consumers,
  escaping views, effectful/fallible/unknown callbacks, computed offsets,
  empty interior, unsupported dtype/codec/backend and unknown source identity
  are explicit pilot declines. Existing fallbacks retain their own semantics.

No additional runner job is required to establish this source census: the
existing saved artifact supplies emission evidence. If root admits a provider
session prerequisite, request a diagnostic-only build with counters scoped to
this one stencil: requested windows/chunk IDs, physical reads, hits, maximum
resident chunk capacities, window capacity, entry count and validation windows.
For the N=10/C=4 control expect requests 2+3+2, seven reads without reuse, and
three reads with certified reuse; validation remains three windows. Keep
diagnostic counters out of timing runs. Compare both cached and uncached
**stream-window** paths, plus the independent values and paired failures above.
Record source mutation/identity refusal explicitly. No runtime or measurement
claim has been established by this card.
