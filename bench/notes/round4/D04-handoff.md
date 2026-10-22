# D04 frozen handoff: rank-one streamed window addressing

State: one approved safety prerequisite implemented; **all build, emission and
runtime validation remain unrun by the coder**. Root is final reviewer; Luna is
the sole runner. No commit requested for this card. Lane branch is
`codex/round4-d-streaming`, HEAD `d13883acaf50a1dba57705f8f3689fe59fd72df2`
(D03 document commit, after accepted D02 `ec75ae38`). Stop after this freeze.

The historical approved design is `D04-design.md`, SHA256
`ABCBE0C60967267B9FA5B3CB281DCE7089981CE52C2539BC02FD3C9F9BCCBD95`.
Its review-pending status describes that earlier checkpoint; root subsequently
authorized this implementation. D03's cache refusal remains in force.

## Exact modified files

- `src/CodeGenCuda.fs`: certify rank-one segment reads before source masking
  or rendering; check signed geometry and allocation capacities; select the
  window-aware renderer; delete the invalid rank-one Array alias. Rank-two row
  aliases retain their existing path. GenStreamRows is unchanged.
- `src/CodeGenLoopNest.fs`: private immutable Rank1WindowReads record,
  complete scalar/read/capture/type admission, exact reference-keyed IRIndex
  substitutions and scalar ElementBinding peels; private renderer core with
  existing public wrappers passing no certificate.
- `tests/ZarrTests.fs`: augment existing stencil/elementwise route assertions;
  author rank1WindowAddressingE2E and backend/stale-certificate refusals.
- `bench/notes/round4/D04-design.md`: approved design snapshot, new on this
  branch; unchanged since root approval.
- `bench/notes/round4/D04-handoff.md`: this freeze and runner request.

No provider/runtime/IR/shared type helper or project-order changes. No A01/C01
branch imported. The D01 temporary local type supplement is untouched; root
already removed it on integration using C01's shared roots.

## Safety and semantics

For each nonempty window [L,H), the checked center/reach geometry proves every
admitted global coordinate c+o lies in [L,H). Loads use
`win[size_t((int64_t(c)+o)-int64_t(L))]`; scalar operand peels use the zero-reach
version. No pointer before the window allocation is formed. Global window
parameters/output ordinals are unchanged. Original Int64 literal offsets are
checked before narrowing; IRAccess's int-valued Offset is not used as proof.
BigInteger checks precede signed endpoint conversion/window allocation and
check the required output capacity. Elementwise output overflow is refused
before constructing its existing run list.

The admitted body is closed scalar arithmetic/conditionals/lets plus certified
loads and nonrecursive capture-free scalar callees. Every other source use,
array escape/forwarding, opaque source-capturing call, unsupported renderer
form, nonliteral extent or dependent/side-table type refuses with a spanned
BL7001 before the segment renderer. Source capture descriptors may carry the
normal enclosing-let mutable flag, but every occurrence still must be a
certified load; source mutation is outside the grammar. Other mutable/array
captures refuse. Callee signatures, bodies and their embedded type extents are
checked too. Whole-source omission/reachability is not claimed or needed.

The current O(N/C) run table, window allocation owner, provider call order,
full padded chunk decoding, ghost copying, per-window missing scan and cleanup
remain. Rank-one source sentinels remain active; successful local substitutions
avoid rendering the source base. No silent `.read` fallback changes validation
order. No cache, prefetch, snapshot, cursor, new donor ownership or speed/peak
claim. Required resident output allocation and printing stay present.

## Exact post-certificate transformations

1. The final kernel has already passed routeKernelBodyThroughCall and the
   statement-form rerouting before certification. Context source removal and
   rank-two alias masking change no kernel/binding IR.
2. The array-output host path sets only `MpiSlab=true` on a copy of codeGen so
   the existing run wrapper controls outer bounds. Actual MPI/device segment
   emission is explicitly refused for this rank-one path.
3. Existing hot-row/flat/compact epilogue handovers may attach plans; a
   rank-one certificate plus any such plan is explicitly refused before loop
   rendering. No alternative epilogue renderer is treated as certified.
4. A segment plan bypasses BLAS, row-jam and flat-elementwise dispatch and
   calls genLoopNestWindowed. Its private core requires the exact kernel
   object, exact binding record objects and slab mode before any rendering.
   There is no later kernel/binding rebuilding on this route.
5. The scalar kernel renders with substitutions after final param/capture
   name mapping. Reynolds and array-valued kernel results refuse, so neither
   the permutation renderer nor the separate literal-row emptySubst renderer
   can bypass the certificate. Existing public renderer wrappers pass None.

## Authored focused assertions, all unrun

- Existing radius-one/radius-two stencil and map/broadcast/resident-zip tests
  assert actual local loads, no invalid alias and their independent values.
- N=11,C=4 square data, v2/v3, Float64/Float32/i4-to-Int64: differences
  [8,12,16,20,24,28,32,36,40], plus padded NaN tail controls. Global-coordinate
  output is [5,11,19,29,41,55,71,89,109], crossing nonzero window origins.
- Zero/singleton elementwise outputs; finite omitted-chunk fill with the
  independent [2,2,-4,-5,0,0,10,11] pin; required output allocation and window
  delete owner assertions. A capture-free named scalar gap helper must render
  actual local-load arguments and produce nine 2s.
- Paired earlier-NaN/later-short and earlier-NaN/later-null cases: chunk 1
  failure wins inside the first window; future chunk 2 is not touched before
  first-window BL8015. Later missing data reports global index 6. Failed reads
  publish no d output.
- Computed/Int64.MaxValue offsets, foreign window parameter, mixed global
  read, source escape, hidden type extent, Reynolds, captured callee,
  array-valued result and source extent disagreement refuse with BL7001 and
  no returned runnable fallback. Repeated normal emission has local loads
  and no error sentinel. Changed kernel/binding records reject a stale
  certificate before rendering, with slab mode already valid in both tests.
- New dtype/failure tuple cases use separate typed lists and explicit
  separators. Print/tile environment and temporary analysis context restore
  in finally blocks.

## Sole-runner request and skip expectations

Runner history: the first freeze (handoff SHA256 beginning `94237102`, test
SHA256 `914D35638B7F62C35631975BDF82B0C4AB8C4763F9A12E5FBF902CEFA23B79BD`)
matched its manifest, but Release build failed before any tests ran. Full log:
`bench/.build/D04-runner/build-94237102.log`. Eight parser errors originated
at the nested record/Map.map lambda in the synthetic source-extent-disagreement
fixture (old lines 1849-1851, first FS0058/FS0611); the four NU1900 warnings
were package vulnerability-service access warnings. The test-only repair
replaces that nesting with typed changedProvider/changedModule helpers and
separate changed-indices/type/read-map locals. The provider extent is still
changed to 12 and the same refusal assertion remains. Both production hashes
are unchanged. All new record/list/function layouts were re-read statically;
this repaired freeze has not been built or executed by the coder.

The second runner attempt used handoff SHA256 beginning `7A697E48` and test
SHA256 `14C85F7F91113941F62DFF1BDEB659E0C4BB6F642C625D4E44EE3BA503B4F44A`.
Its Release build reached two test-only errors, with no category run: ambiguous
File.WriteAllText overload in the local run helper (FS0041), and an omitted
source-list separator after sprintf in the zero/singleton fixture (FS0001).
Full log: `bench/.build/D04-runner/build-7A697E48.log`; the same four NU1900
warnings remain environmental. The current test-only repair annotates run's
label/cpp strings and source/emit helper inputs, and makes both new multiline
String.concat inputs separately typed string lists with explicit semicolons
after every entry. Fixture/test intent and both production hashes are unchanged.
No coder execution or new validation claim accompanies this third freeze.

After root source review, Luna should verify this exact manifest, build this
checkout's Release Blade.fsproj once, then run **only standalone `test zarr`**
from a private D04 working directory (for example bench/.build/D04-runner).
Do not run a whole suite, perf benchmark, additional compiler experiment or
memory/sanitizer category for this card. Retain full build/category logs,
category totals and exact skips; report raw failing emitted snippets if any.

Expected new runtime assertions require g++; with the campaign toolchain they
should execute. Missing g++ skips must be reported explicitly rather than
counted as coverage. The previously accepted Zarr category had one internal
NetCDF library differential skip; that existing skip is expected if the same
optional library remains unavailable. No other new skip is intended. Root
controls subsequent integration and any broader round-end validation.

## Frozen working-file SHA256

| File | SHA256 |
|---|---|
| src/CodeGenCuda.fs | 5C6F99CA6C29A1171991EEECBBFCE9B79C65F8CCF150F53EBF401AE8F75AA96E |
| src/CodeGenLoopNest.fs | FEDD661A83B58BEAB080CECBBE57C5E783D24BF9D1333479C45AA586AF365AF3 |
| tests/ZarrTests.fs | B1034A90C5C9E3ED1AD2A98BC18C631A50C9D5D85F98613C6CE7929611BF9F5F |
| src/providers/ZarrProvider.fs (unchanged control) | 82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80 |

Static verification only: read the complete new test block and production
diff; git diff --check passes; tracked source files retain index LF/worktree
CRLF. Compilation, route emission and runtime behavior are not yet verified.
Unsupported rank-one forms that formerly reached the invalid alias now refuse;
this deliberate safety boundary is a limitation, not a safe legacy fallback.
