# D05 frozen handoff: compact uniform rank-one halo traversal

Status: implementation candidate frozen for root review and Luna validation. Base HEAD is `fcd135f444fcceced619e3ae6ce151b00448f6ac` on `codex/round4-d-streaming`. No commit was made. The earlier D05-census.md is the design-only checkpoint that root subsequently approved; its source hashes describe that earlier snapshot.

## Change and boundary

- `src/Types.fs`: SegmentTable stores `UniformRuns(extent, edge)` or `ExplicitRuns(offsets)`. The existing record/tryFind offset API remains available and explicitly expands uniform runs only when a caller requests compatibility offsets. New recordUniform/tryFindRuns preserve compact evidence; reset clears both forms and qualified aliases remain distinct.
- `src/TypeCheckInfer.fs`: exact single-level `[SegRegular K]` registration records the compact descriptor without calling segmentationOffsets. Other segmentation forms retain explicit registration. Both authored literal K and store-inherited K follow this route.
- `src/CodeGenCuda.fs`: only the existing certified rank-one dense streamed Zarr halo path consumes the compact descriptor directly. It emits a capped-step arithmetic cursor instead of a boundary array. Other paths consciously expand/use ExplicitRuns. Logical K is retained independently of physical provider chunk C. The D04 certificate, local reads, required resident output and provider calls remain in place.
- `tests/ZarrTests.fs`: update the D04 emission witness for the cursor and add uniformHaloCursorE2E, covering descriptor identity/reset, independent geometry/capacity pins, authored/store K, values, local loads, clamps, progress, cleanup, explicit-path controls, malformed evidence and failure precedence.
- `bench/notes/round4/D05-census.md`: source/design checkpoint; `D05-handoff.md`: this final implementation handoff.

No provider runtime, LoopNest renderer, cache, prefetch, source identity/snapshot, or cursor over provider files was added. The D01 temporary local type supplement was not changed. No other lane was imported.

## Geometry and ordering

For each raw interval, step=min(K,N-lo), hi=lo+step and the cursor advances to hi before any empty-window continue. Positive K and remaining extent imply positive progress and hi<=N without uncapped addition. Thus raw runs, clipped empty prefixes/tails, clamps and global coordinates match the explicit uniform boundaries.

The pure capacity helper computes the exact maximum core width from the first partial run, presence of a full middle run and final partial run, then adds signed halo reach using BigInteger checks before narrowing. It checks positive extent/edge, edge<=extent, reach signs and exact center bounds. Empty center extent reserves capacity one. Existing output and window byte-capacity guards remain. The frontend zero-extent Chunked rejection is unchanged.

Every admitted nonempty window retains its existing provider read and per-window validation before kernel evaluation; no reads or validations are skipped. With physical C=4 and logical K=3, an early missing value can win before a later short/null chunk is reached; K=4 can encounter that later chunk inside its first window. The paired assertions deliberately preserve this distinction. Existing owners and deletion order are unchanged.

## Scope of the memory claim

The admitted uniform traversal descriptor and emitted traversal metadata are O(1) in the number of runs. This is not a total compiler-memory or total program-memory claim. Required output remains resident. Provider storeFiles/fingerprint discovery still enumerates chunk files. Explicit segments()/IRSegments consumers, irregular/multilevel/file forms and compatibility expansion can still hold O(number of runs) offsets. Rank-two, elementwise and other-provider paths retain their previous explicit behavior/refusals. There is no performance claim.

## Authored checks; none executed by coder

The new focused block checks small independently pinned run sequences/capacities, including N=10/11 odd tails, K=3 over C=4, wide radius, one-sided reach and an empty interior. Small square-valued fixtures pin exact halo differences and actual local indexing. It asserts no boundary table on the admitted route, exact window allocation, capped progress before continue, unchanged clamps/output/cleanup, explicit irregular and segments() controls, malformed descriptors and signed/capacity refusal. Existing D04 assertions remain active.

Metadata-only shapes N=1009 and N=1,000,000,007 with K=127 contain metadata only and are lowered/emitted to compare compact descriptors and bounded generated-source growth. The Int64.MaxValue metadata-only shape is lowered/emitted only to assert allocation-capacity refusal. Neither large shape may be compiled or executed as resident-output programs. No payload is generated for them. The pure signed-limit geometry assertions do not enumerate offsets.

Requested sole-runner gate: one Release build of this exact freeze, then standalone `test zarr` from a private D05 working directory. Do not run a full suite or benchmark for this card. The normal campaign toolchain should execute the small generated-C++ value/failure cases; report any toolchain skips. The existing internal NetCDF differential may skip when its library is unavailable (one such skip in D04). No new intentional runtime skip is introduced. Any compiler/build/test outcome remains pending; the coder performed static reads, diff checks and hashing only.

## Frozen SHA256

| File | SHA256 |
|---|---|
| src/Types.fs | 78E7C0BD662B7EEA7640FDA0E39C8891A8968A37A788C45CFB7130D0A340AE3A |
| src/TypeCheckInfer.fs | DE01C2129AFD253BA53998EE78E964AA1DE2709E708A1AFDE7AB7E2AD3A195D3 |
| src/CodeGenCuda.fs | 467C733B3F95D321ADB17E271C6E5110D7DC97E7549F4EBA08D674F67E09EF5C |
| tests/ZarrTests.fs | 071018D5D171C7ABDB1A4A8FD7ED8D36E968ED16EEB123A93404976C0C6F795A |
| bench/notes/round4/D05-census.md | B0DFB36E094869EE8FD6D1861BB30E7A339A0FE3AEEDA4FF1F9CC554BE5F2F74 |
| src/CodeGenLoopNest.fs (unchanged) | FEDD661A83B58BEAB080CECBBE57C5E783D24BF9D1333479C45AA586AF365AF3 |
| src/providers/ZarrProvider.fs (unchanged) | 82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80 |

Static final review: no whitespace errors in git diff; the four modified tracked files retain LF index / CRLF working-tree classification. No build, emission, execution, test or benchmark was performed by coder. Freeze ends this one card pending root review and the sole runner.
