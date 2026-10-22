# D09 proposal: eliminate redundant full-height rank-two input row tables

Status: **source census/design only; implementation and probes await root review**.
Only this note is authored. No build, lowering, emission, compilation, execution,
tests, benchmark, Git mutation or delegated work was performed. D08 production
remains frozen; its 82/0/0 focused result was reported by root.

Graft `UniformRuns` retrieval found no F# coverage among its 68 indexed files;
targeted source reads followed. The lane source at `717790c4` and integration
were compared without changing either checkout. The rank-two CodeGenCuda path
is identical; integration's normal window reader lacks D08's rank-one-only
borrowed-buffer parameter, which does not change the rank-two census below.
Anchors below refer to the integration snapshot read for this note.

## One highest-value candidate, and the remaining obligation

Remove the **streamed inputs'** `new T*[N]()` row tables for one ordinary dense
rank-two scalar pointwise map. Replace each certified outer operand peel with
an address inside its existing contiguous filled band. Keep the required
materialized output, its allocator/layout, existing data bands, provider reads
and validation. This removes redundant metadata, not a source-data copy.

Do **not** include arithmetic-cursor work in this card. The compiler's explicit
run list and emitted `const size_t out__runs[G+1]` remain O(G), G=ceil(N/K).
Required rank-two output storage itself retains an O(N) row skeleton.
Compiler-side store-file/fingerprint enumeration also remains unbounded in file
count. Therefore this is not bounded total metadata, compiler memory or RSS.
No gain has been measured or is claimed.

## Existing reachable route and concrete allocation cause

The existing idiom is already tested at ZarrTests.fs:2113-2173: dense
`T |> z.stream`, `doubled = T * 2.0`, with an ordinary resident rank-two output.
That test also uses tiles/group_by/ungroup, which are outside this proposal.
The isolated pointwise witness below needs no manual tiling API or Chunked alias.

| Actual source seam | Baseline obligation |
|---|---|
| CodeGenCuda.fs:3620-3664 | Scalar parameters over rank-two streamed operands select elementwise row bands; a fiber parameter stays on the older fiber route. Sources retain operand order, deduplicated by emitted source name. Block size comes from the first source's StreamRowsBlock. The compiler eagerly constructs `[0; K; ...; N]` as ExplicitRuns. |
| ZarrProvider.fs:2709-2712 | StreamRowsBlock returns the physical leading chunk edge, otherwise 4096. This is not a user-authored uniform Chunked descriptor. |
| CodeGenCuda.fs:3665-3741 | D04/D05's certificate and checked capacity are rank-one-only. Rank two has no corresponding complete local-read certificate. |
| CodeGenCuda.fs:4231-4281 | maxWin is scanned from the explicit offsets. The offsets become an automatic C++ boundary array, then an indexed run loop. D05 UniformRuns is accepted here only with its rank-one certificate. |
| CodeGenCuda.fs:4282-4314 | Per streamed input: `T* win = new T[maxWin*M]`, `T** rows = new T*[N]()`, and two source extents. After each read, only live global rows are assigned `rows[r] = win + (r-w_lo)*M`; an `Array<T,2>{rows,extents}` aliases that full-height table. Each table is deleted once after the run loop. |
| CodeGenLoopNest.fs:402-461,489-522,2240-2284 | Ordinary rank-two peels read the outer row through `.data[global_i]`, then a scalar through the peeled row's column. restrictPeelSites already distinguishes rows used exclusively by deeper scalar peels. That qualification alone does not prove a streamed source cannot escape elsewhere. |
| ZarrProvider.fs:2071-2096,2106-2197,2234-2244 | Stream prologue checks metadata existence and fill; each GenStreamRows call assembles `[lo,hi) x [0,M)` directly into caller win. It allocates one padded-chunk decode vector, copies intersections/fill, then performs the normal per-window missing scan. No whole-array input pool is needed. |

Allocation ledger for S streamed inputs, element bytes E, disk bytes D_s,
physical chunks C0_s x C1_s, pointer bytes P, W=maxWin=min(K,N) for this pilot:

- Required output: its existing dense rank-two value pool/layout and owned
  row skeleton. It remains resident and printed on demand. In the ordinary
  allocator (nested_array_utilities.hpp:513-545), this is an N*M scalar pool
  plus a separate N-pointer skeleton; neither output allocation is removed.
- Input data bands: S*W*M*E bytes, owned by their existing `new[]`/`delete[]`.
- Redundant input row tables: S*N*P bytes, zero-initialized before any band
  read and filled again for each live row. **Only this term is targeted.**
- Decode vectors: sum_s(C0_s*C1_s*D_s) capacity. Successive source reads are
  emitted into the same run-loop body; their vectors coexist through consumer
  evaluation, not merely the maximum one-source decode capacity.
- Run boundary array: (G+1)*sizeof(size_t), plus O(G) compiler list/pairwise/
  initializer-string work. Source extent tables and loop scalars are fixed-size.
- Provider stream/string internals, allocator metadata and file enumeration are
  separate terms. Emitted allocation counts are source facts; optimized object
  placement and actual peak memory have not been observed.

For the small fixture below (N5,M3,K2,S2,E=D8,P8), this means two 48-byte bands,
two 40-byte redundant row tables, two 32-byte decode vectors and four boundary
words. Removing the redundant row tables would remove 80 emitted heap bytes in
this example; it does not remove either 48-byte value band, either 32-byte decode
capacity, the run table or the required output. This is a design calculation.

## Proposed one-card admission and safe addressing contract

Initial pilot: one immediate materializing scalar pointwise map/zip with one or
two direct local Zarr inputs, static identical N x M shapes, unitless Float64,
plain dense S-dimensions, identity codec, no packed layout/mask/window view,
ordinary missing-value policy, and a common positive physical leading chunk
edge K. Start with v2; v3 remains a control unless the observed topology/provider
capability is identical and root admits it explicitly. Output has two ordinary
dense dimensions and scalar cells. No change to frontend syntax or provider API.

The later emission probe must establish the exact lowered zip/peel topology
before coding the certificate. Do not infer it from names or source syntax.
Proposed certificate obligations:

1. Bind source IR IDs/specs/types, the final kernel and actual loop-binding/
   ElementBinding objects, input positions and both row/column peel sites.
   Two unshifted dense levels, scalar input/output ranks, no shared/fused/
   symmetric/dependent/halo iteration, scheduling epilogue, fold wrapper,
   Reynolds, MPI/device or thread parallel form in the first pilot.
2. Prove each input's exact type/identity in its own role, loop and ApplyInfo
   metadata consistent with those inputs, and actual output shape/layout.
   Scalar map outputs may have fresh index IDs, as D08's observed topology
   established; never replace anonymous provenance globally with extent equality.
3. Walk the complete final body, captures, resolved callees, embedded type/
   extent payloads, side uses and emitted bounds/guards. Every streamed-source
   occurrence must be consumed by the certified row-to-scalar peel chain.
   Initially use a closed arithmetic scalar kernel with no source capture,
   gather/index expression, assignment, array-valued result or array argument
   forwarded to a helper. Literal passive extents only. Any needed source-name
   use in a guard must be accounted for before removing the Array alias.
4. Certify at the actual emission seam, before masking source sentinels or
   dropping row owners. Recheck final kernel/binding/peel object identity after
   transformations, including the final rendered parameter names. A stale plan
   declines before omission. Never allow an alternative renderer to require the
   removed source wrapper, or rely only on a storage-name substring test.
5. Check static geometry and products in wide arithmetic before narrowing or
   allocation: N,M,K positive; W<=N; output N*M*E and N*P allocations, and each
   input W*M*E allocation, fit the actual allocator/
   pointer-difference range. The existing provider narrows shape/chunks to int
   and multiplies chunkCount/chunkBytes there: every admitted shape/chunk extent,
   chunk product and decoded byte count must fit those actual int limits too.
   Do not rely on unchecked int64 multiplication in the existing band emitter.

The complete replacement is at the **outer peel**, for example:

```cpp
// Existing globals: w_lo <= i < w_hi, and 0 <= j < M.
double* local_row = win + (i - w_lo) * M;
double cell = local_row[j];
```

Use checked signed/wide evidence before emitting the size_t difference/product.
For each filled band, `0 <= i-w_lo < w_hi-w_lo <= W`, so the row starts inside
the allocation and its scalar access offset is below W*M. Keep global i/j for
output indices and diagnostics. No `win-w_lo*M`, `rows-w_lo`, pointer before
allocation, or globally indexed undersized pointer table is permitted. The
outer pointer is non-owning; retain exactly the existing win owner/deletion.
Remove only rows allocation, live-row assignment loop, source Array wrapper
and rows deletion once all their uses are certified.

CodeGenLoopNest.fs:1512-1675,1677-1686,2269-2273,2621-2623 supplies the D04
reference-keyed certificate/substitution precedent. Its present scalar rank-one
peel recipe cannot simply be reused for a rank-two row. A separate small row-
peel certificate/internal emitter entry is preferable to changing public
genElementBindingNew or pretending a rank-one certificate covers rank two.
Expected source ownership if approved: CodeGenCuda.fs band preflight/owners,
CodeGenLoopNest.fs certified outer peel, ZarrTests.fs focused assertions. No
Types/SegmentTable/provider/core allocator change is required for this card.

Unsupported gathers/escapes in a would-be streamed band must produce a deliberate
spanned backend refusal when locality cannot be proved. The full-height table
contains pointers only for previously/currently filled bands; it is not evidence
that a global gather is valid. Do not fall back from failed locality proof to a
runnable unsafe/stale alias. Existing separate fiber/group/tile routes remain
outside this optimization and retain their own supported behavior. Other
providers/dtypes/layouts/rank>=3, mismatched shapes/chunks, zero extents and
irregular/multifile forms are explicit non-admissions, not claimed wins.

## Original validation and failure sequence must remain

For each original raw run, preserve the same center/window clamps and source
order. For the proposed ordinary band, c/w interval is exactly `[lo,hi)`;
GenStreamRows reads all trailing columns. Source A's intersecting chunks are
opened in current c0/c1 grid order, read whole, copied/filled, then its completed
window is scanned. B is read/scanned next; only then does the consumer evaluate
cells and write the resident output. The decode vectors stay in their current
scope. Do not interleave computation with partial read/scan or hoist provider
allocation/I/O as part of row-table elimination.

Consequences to pin later: a later short/null chunk in A's SAME window beats an
earlier A nonfinite value because the scan is after assembly. An A missing scan
beats a B read failure in that band, and an earlier-band A missing scan beats a
future-band corrupt chunk. Diagnostics retain global `(row,column)` origin,
including a nonzero/final band origin. Finite fill stays finite; physical padded
tail cells remain outside the logical scan. Preserve metadata-prologue timing,
source-data error priority, required output and existing cleanup. OOM identity
is not promised when an allocation is intentionally removed.

## Minimal later topology/emission probe for root approval

One fresh private N5 x M3 Zarr v2 store with two variables A/B, both chunks2x2,
little-endian f8, C order, no compressor/filters, finite fill0, dimensions
`[row,col]`. Six padded 32-byte chunks per variable (384 payload bytes total).
Logical values: A(i,j)=10*i+j, B(i,j)=100+10*i+j. Do not create a large store or
run any generated program. Use a source-matching reviewed DLL and the already
proved FSI host-directory setup if necessary; no options/disk policy is needed.

Common input source and positive materialized-output source:

```blade
import zarr as z
let s = z.load("<absolute_private_fixture>")
let A = s.vars.A |> z.stream
let B = s.vars.B |> z.stream
let out = (method_for(zip(A, B)) <@> lambda(a, b) -> a + 2.0 * b) |> compute
```

Select only `out` for printing: it is a demanded resident array, not a removable
intermediate. Independent output (later native pin):
`[[200,203,206],[230,233,236],[260,263,266],[290,293,296],[320,323,326]]`, displayed
as Float64 cells. Runs must be `[0,2),[2,4),[4,5)`; chunk keys per source are
`0.0,0.1`, then `1.0,1.1`, then `2.0,2.1`. Final row uses local offset0, not4.
Expected twelve chunk-open attempts across both sources, excluding metadata.

Use the same fixture for three small source controls, with separate lowering/
decision capture and no native execution:

- Scalar-terminal control replaces the final declaration with
  `let term = reduce(method_for(zip(A, B)) <@> lambda(a, b) -> a + 2.0 * b, (+), axes = 2)`;
  print term. Its independent value is 3945.0, but this probe must report actual
  materialization/fold topology rather than assume it uses the output route.
- Gather control replaces the scalar body with `a + B(0, 0)`; print out. This
  requires global row0 even in later bands. Record current acceptance/refusal;
  future row-table elision must refuse the hidden nonlocal source read.
- Escape control replaces the body with `B` (array-valued captured result).
  Record the earliest lower/validate/backend refusal or actual output-rank/
  capture topology. Future locality admission must refuse it; do not run it.

Requested later artifact: one reviewed source-only bridge, authored before Luna
dispatch, using existing lowerCaptured/validateIR/whole-program emission APIs.
Save complete decisions/reasons first, then typed source specs, output demand,
callable/capture/extent/ApplyInfo records, actual LoopIndexBinding/ElementBinding
chains if accessible, and full generated C++/warnings/refusals. Highlight row
table allocations/initialization/deletes, outer row/inner scalar reads, maxWin,
run table, source read/scan order and output allocation. Full IR dumps should
be isolated so serialization failure cannot discard reasons. No compiler,
runtime or test implementation is approved by this note, and Luna should not
invent diagnostic APIs or run a category to obtain this graph.

After that evidence and root implementation approval, focused native checks
would cover the exact array values including the last/nonzero-origin band,
source-order reversal, finite fill, same-/future-window short/null/nonfinite
failure pairs with global coordinates, adversarial gathers/escapes/stale
certificates and retained fiber/tile/terminal controls. Assert actual absence
of both input row tables/deletions, presence of checked local row loads, and
presence of the deliberately retained run table and required output allocation.
No performance or end-to-end memory claim follows from these structural tests.

Source identity for this census: CodeGenCuda SHA256
`467C733B3F95D321ADB17E271C6E5110D7DC97E7549F4EBA08D674F67E09EF5C`;
integration CodeGenLoopNest
`CE9359328D4C3207EAE24C27F2B77BE9698B76C87A35159AC7B00F36186B3E03`;
integration ZarrProvider
`82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80`.
D08's eventual merge may change the latter files' hashes without changing these
rank-two sites; refresh anchors and certificate ownership before implementation.
