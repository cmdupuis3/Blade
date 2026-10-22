# D05 / PM42: uniform segment metadata and arithmetic cursor census

State: **source/design only; no implementation or execution authorized yet**.
Basis: accepted D04 `fcd135f444fcceced619e3ae6ce151b00448f6ac` on
`codex/round4-d-streaming` (551 Zarr passes, zero failures, one internal NetCDF
skip were reported by root for D04, not run for D05). Graft SegmentTable query
found no F# coverage; targeted source reads supplied this census. No production
or test changes, builds, emission, compiler execution or benchmark for D05.

## Concrete source cause, including compiler memory

The existing source witness remains ordinary Blade:

```blade
import zarr as z
let s = z.load("a_dense_rank1_zarr_store")
type CX = Chunked<s.index.x, store>
let A: Array<Float like CX> = s.vars.A |> z.stream
let d = method_for(halo<CX, [-1, 0, 1]>) <@>
    lambda(w) -> A(w(1)) - A(w(-1)) |> compute
```

No manual tile API is needed. There are distinct metadata terms:

| Phase / source anchor | Current representation and growth |
|---|---|
| TypeEnv.fs:112-133 | Segmentation is already compact for this form: static N, Source index record, Levels=[SegRegular K]. O(1) words per uniform alias. |
| TypeEnv.fs:137-163 | segmentationOffsets eagerly builds interior boundaries, pairs, run tuples and the final list [0;...;N], O(G), G=ceil(N/K). |
| TypeCheckInfer.fs:18658-18663 | Every Chunked declaration calls segmentationOffsets before recording Types.SegmentTable, even when the program never calls segments(). Thus the halo-only witness already allocates O(G) compiler metadata. |
| Types.fs:488-500 | SegmentTable is an AsyncLocal map from alias to int64 list; only its own registration/reset and the halo consumer need this registry. |
| CodeGenCuda.fs:3560, 3642-3681 | Halo segmentRun retrieves that list; D04 checks all boundaries and folds all clipped runs to find exact maxWin. More O(G) traversal/intermediate metadata. |
| CodeGenCuda.fs:4212-4266 | Builds the comma-separated literals and emits const size_t output__runs[G+1], then indexes it for every run. Generated source/initializer size is O(G); the C++ object has automatic storage in the abstract program. Optimizers may place/elide it, so actual resident bytes are not measured or guaranteed by this census. |
| TypeLower.fs:1814-1840 | The dense halo type tag contains alias name plus declared radius offsets, and the axis extent is N-shrink. It does not encode all G boundaries. O(radius-count), fixed for this pilot. |

The static run table is therefore not a free runtime constant bound. Replacing
only its C++ loop leaves the eager compiler list intact. Iterating that list
lazily after construction also leaves the O(G) allocation intact.

Explicit grouping is a separate retained term: TypeCheckInfer.fs:5420-5432
calls segmentationOffsets for segments(CX); TypedAst.fs:313 and IR.fs:190
carry the resulting list; Lowering.fs:956-957 preserves it; CodeGenBinding.fs:
3621-3638 emits another offsets array. Grid/file/grouping consumers also
expand lists (TypeCheckInfer.fs:5448-5449, 5470+, 5551, 5564). D05 must not
claim to bound those consumers by changing the halo traversal alone.

There is another independent compiler term: ZarrProvider.fs:1271-1299
storeFiles uses Directory.GetFiles, sorts paths and retains a list for
fingerprint/version-stamp work; fingerprinting also reads each file. Removing
segmentation lists does not bound all compiler memory with respect to chunk
file count. This card can bound **uniform traversal metadata** through checking
and emission; it cannot claim constant total compiler memory or bounded total
program memory (the required output remains resident).

## Narrow proposed admission and representation

First route: one local dense rank-one Zarr source, the existing D04-certified
scalar dense halo, and a Chunked alias whose registered Levels is exactly
[SegRegular K] with static N and positive checked K. Store-inherited and
literal edges have the same uniform-grid meaning; a first implementation can
admit the store-inherited witness and must retain authored K for any admitted
literal-edge form. No runtime source identity/immutability contract is needed:
the same windows are still read afresh in the same order.

TypeCheckInfer.fs:18363-18382 already provides this evidence: literal K is
checked in 1..N; `store` inheritance uses DimChunks for the named axis and
refuses conflicting/missing edges. ZarrProvider.fs:1078-1092 derives DimChunks
only for dimensions uniformly chunked across dense variables. SegFiles has a
different meaning, including file-local chunk restarts and short tails.

Proposed one-card seam, subject to root review:

1. Add a compact registry entry in Types.SegmentTable such as
   UniformRuns(N,K) | ExplicitRuns(offsets). Types is before TypeEnv/IR, so this
   entry must contain only primitive grid facts; do not move Segmentation there.
   Keep the existing per-compilation AsyncLocal reset/alias-key discipline.
2. At the existing declaration registration, match [SegRegular K] and record
   N,K directly. Do **not** call segmentationOffsets on this branch. Other
   levels record their existing explicit offsets. TypeEnv.Segmentations already
   stores the regular form compactly; its structure needs no redesign.
3. Let the halo segment plan retain the compact entry through D04's geometry
   admission to the run wrapper. Use arithmetic geometry/capacity checks below
   and emit a cursor instead of the G-boundary string/table. The D04 read
   certificate/local load renderer, provider window reader, source shape,
   required output allocation, window owner and cleanup remain unchanged.
4. Non-admitted uniform provider/rank/forms may explicitly expand on demand for
   their existing safe path; irregular/file entries keep the explicit path and
   its existing refusal behavior. Such fallback remains unbounded metadata and
   must not be counted as an admitted bounded traversal. No first-difference
   inference from an arbitrary list or silent coercion of SegFiles to Uniform.

Expected production ownership if approved: Types.fs (registry entry/API), the
small registration arm in TypeCheckInfer.fs, and the segment-plan/geometry/run
wrapper in CodeGenCuda.fs; ZarrTests.fs for focused assertions. No provider
implementation, user-facing language API or new IR expression is needed for
this halo-only route. Explicit segments()/files()/group_by()/grid APIs stay
outside this card. A program using those may still get a bounded halo cursor,
but its other grouping metadata remains O(G), explicitly.

The elementwise branch at CodeGenCuda.fs:3637 independently constructs runs
from ProviderSpec.StreamRowsBlock. Zarr's implementation at 2709-2712 returns
the physical leading chunk edge. It is another useful route but is **deferred
for this first halo card**, as are rank-two bands and other providers. Do not
silently change their existing segmentation by sharing the halo's authored K.

## Cursor, progress and exactly preserved clamps

Keep every raw run, including the runs whose clipped center interval is empty.
Advance before the existing continue so empty prefix/tail runs cannot stall:

```cpp
for (size_t next = 0; next < N; ) {
    const size_t raw_lo = next;
    const size_t remaining = N - raw_lo;
    const size_t step = K < remaining ? K : remaining;
    const size_t raw_hi = raw_lo + step;
    next = raw_hi;
    const size_t c_lo = raw_lo < cMin ? cMin : raw_lo;
    const size_t c_hi = raw_hi > cMax ? cMax : raw_hi;
    if (c_lo >= c_hi) continue;
    // Existing checked signed w_lo/w_hi, lastN clamp, GenStreamRows,
    // per-window validation, global slab ordinals and consumer loop unchanged.
}
```

Admission retains D04's checked signed N/reach/start/output/byte capacities.
Require valid uniform provenance, N>=0, K>0, and current frontend K<=N for an
actual Chunked declaration. Its N=0 declaration is currently refused by the
1..N rule; do not broaden that rule in D05. The abstract cursor's N=0 case has
zero iterations and is still useful as a helper boundary check.

When raw_lo<N, remaining>0 and 1<=step<=remaining. Therefore raw_hi>raw_lo,
raw_hi<=N, and the addition cannot overflow the checked integer/index domain.
The next raw boundary is exactly min(raw_lo+K,N), without ever evaluating the
uncapped potentially overflowing sum. No j*K or (N+K-1)/K arithmetic is needed.
Induction gives exactly the old regular boundaries, including a short final
run and termination at N. If a run ordinal is retained for diagnostics, it
advances once per raw run, including empty-center runs; no read is moved.

The existing center clamps, signed window endpoints, lastN clamp and
source-order GenStreamRows calls remain textually/structurally in that order.
Do not jump to the first valid center, stop at cMax instead of N, combine two
windows, prefetch the next chunk, move missing validation, or reuse a decoded
chunk. D04's global w/output coordinates and local filled-window proof remain.

## Exact O(1) maximum window capacity

Avoid replacing the list scan with a different O(G) loop or an oversized
scratch allocation. Let a=cMin=start=-rLo, b=cMax=N-rHi, R=rHi-rLo and M=b-a.
All arithmetic is checked wide before narrowing, as in D04.

- M=0: preserve the current minimum maxWin=1; no window is read.
- M>0: head=min(M, K-(a mod K)); rest=M-head.
  maxCore=K if rest>=K, otherwise max(head,rest).
  maxWin=maxCore+R.

Proof: the first intersected uniform cell contributes head centers. Any
remaining full cell contributes K; if there is no full remaining cell, rest
is the sole final partial cell. This is exactly the largest clipped run
width. Every nonempty window has that width plus R, so the old list-fold
capacity is preserved, including a radius wider than one segment. Check
maxWin*elementBytes against the existing pointer/allocation limit before
emission. The active global interval must satisfy D04's 0<=a<=b<=N; invalid
or overflowing metadata refuses before allocation rather than saturating.

## Hand-derived sequences and controls

All intervals are half-open; '-' means the existing continue performs no read.

| N,K,reach | Raw runs | Clipped centers | Requested windows | maxWin |
|---|---|---|---|---|
| 10,4,[-1,+1] | 0:4; 4:8; 8:10 | 1:4; 4:8; 8:9 | 0:5; 3:9; 7:10 | 6 |
| 11,4,[-1,+1] | 0:4; 4:8; 8:11 | 1:4; 4:8; 8:10 | 0:5; 3:9; 7:11 | 6 |
| 10,3,[-1,+1] | 0:3; 3:6; 6:9; 9:10 | 1:3; 3:6; 6:9; empty | 0:4; 2:7; 5:10; - | 5 |
| 11,2,[-3,+3] | 0:2; 2:4; 4:6; 6:8; 8:10; 10:11 | empty; 3:4; 4:6; 6:8; empty; empty | -; 0:7; 1:9; 3:11; -; - | 8 |
| 2,1,[-1,+1] | 0:1; 1:2 | empty; empty | -; - | 1 |

K is the **language segmentation edge**, not necessarily the physical Zarr
chunk edge C. For N=10,C=4, literal Chunked<...,3> first reads [0,4), whereas
K=4 first reads [0,5). With an earlier NaN at index 0 and short/null physical
chunk 1, K=3 reports first-window missing before visiting chunk 1; K=4 reports
chunk 1's read failure inside its first window. Replacing K with C is therefore
a source-data diagnostic regression, even when successful scalar values agree.

## Proposed focused assertions (not authored or executed)

- The same actual Blade idiom records UniformRuns(N,K), emits no run-offset
  initializer, and uses a capped step. A large metadata-only shape must not
  expand G offsets merely to typecheck/emit this witness; do not allocate a
  huge fixture payload or execute its required resident output.
- Small pure geometry cases compare the exact hand-derived sequences above
  and the old regular-boundary reference, including maxWin, empty centers,
  one-sided reach, exact multiples and odd tails. Boundary cases near signed
  limits verify refusal/progress without pathological allocations. K=0,
  extent disagreement, malformed uniform evidence and overflowing capacity
  refuse; current invalid zero-extent Chunked syntax remains unchanged.
- Retain D04's independent square differences/global-coordinate pins, actual
  local loads, output allocation and cleanup checks. Add K=3 over physical C=4
  to distinguish authored segmentation from the provider block size.
- Retain same/future-window missing vs short/null precedence. The K=3/C=4
  paired failure above is the distinguishing read-order control. No chunk
  read or validation may disappear through this cursor change.
- Explicit segments()/IRSegments, irregular/file-level metadata, rank-two,
  elementwise and non-Zarr paths keep their existing path/refusal and remain
  visibly outside the bounded-traversal claim. No source snapshot/cache
  permission is manufactured. Registry reset/repeated compilation must not
  inherit a grid descriptor from an earlier program.

No diagnostic/runtime experiment is needed to establish this source cause.
If root approves implementation, coder authors the focused assertions and
freezes one diff; Luna alone builds/emits/runs the approved standalone category.

## Source SHA256 for this census

| File | SHA256 |
|---|---|
| src/Types.fs | 00B74D146B646EAE0D11D072D7CCD3E792DBCA3F97E8F91C9DD06432C8D115B9 |
| src/TypeEnv.fs | 2C94B6C6B7177648B1DB2E2095C25F0FCB13B2703E29AA21F195D71C4392C18A |
| src/TypeCheckInfer.fs | 112ACC28D75952EEF5E3E59F9418261941988F3F6125A26CC53E0CACDC8B40CD |
| src/TypeLower.fs | FB5D34FDA95031935AC1A82E9BBA2667F8D5698292889C3AF790ABA00631443F |
| src/CodeGenCuda.fs | 5C6F99CA6C29A1171991EEECBBFCE9B79C65F8CCF150F53EBF401AE8F75AA96E |
| src/providers/ZarrProvider.fs | 82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80 |
| src/providers/ProviderRegistry.fs | C46C4B532251AD6AF3C10BEEED51C819EA3B0E1322C2DD80E8E694333945AE66 |

Only this document is changed for D05. No performance or total-memory result
is claimed; this is a concrete proposed traversal-metadata prerequisite.
