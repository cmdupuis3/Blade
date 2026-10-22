# D09-I2 frozen implementation handoff

Status: authored and statically reviewed, UNRUN. No build, lowering, emission,
native compilation/execution, test or benchmark was performed by this lane.
No commit. Base HEAD: 8ab4cb4566ccb9131ff2175abb8d2748fb2a7d4e.
This is the last kernel checkpoint; root reviews before sole-Luna validation.

## Exact scope and ownership

- src/CodeGenLoopNest.fs: opaque internal Rank2BandPeels with private record
  construction; complete narrow preflight; stale-proof checked renderer;
  exact reference-keyed outer row-peel replacement. Existing public wrappers
  and rank-one certificate behavior keep their signatures/defaults.
- src/CodeGenCuda.fs: candidate only at the final ordinary host array renderer,
  after MpiSlab and all epilogue attachments. Successful checked rendering
  carries the same plan into the window owner builder. Only then omit input
  row tables, their installations/deletions, source Array aliases and their
  fixed private extent tables. All nonadmissions retain I1 scaffolding.
- tests/ZarrTests.fs: extend the existing separately callable focused gate;
  update admitted route assertions and add independent fallback witnesses,
  geometry, identity, stale-proof and arithmetic checks.
- D09-I2-runner-inputs/run-focused.fsx: simple bridge to that compiled gate,
  derived from the reviewed I1 bridge. It preserves the host-directory override,
  assembly/header hashes, hidden bounded native runner and private case root.
- This handoff and the separate D09-I2-source-hashes.txt manifest.

No Optimize, provider/runtime, framework, Types, SegmentTable or project change.
I1 historical handoff/manifest/bridge and all previous artifacts remain intact.
The old lane CodeGenState explanatory comment is deliberately untouched;
root already corrected it in integration.

## Certificate and address proof

Only two direct streamed operands, plain rank-two shared axes, scalar F64
capture-free kernel/callee closure, local v2 identity f8 Zarr, matching positive
shape/common leading chunk edge. No mixed resident source, unary, capture,
array/row return, fold, Reynolds, strategy, epilogue or hidden payload admission.
Every IRMethodFor/Apply payload, source spec/IRVar/type/identity/name and both
positional final peel pairs must agree. The observed zip has two final levels,
but MethodFor.TotalSDims=4 and four SCNeither/false entries: this follows raw
d9p typed IR rather than assuming those payloads count shared levels.

The proof retains the final immutable CG object (therefore its kernel,
binding/inner/outer site references), complete names map and resolved callable
record references. Stale renderer entry returns Error before mutating emission
channels. A subsequent invariant failure is a hard internal failure, never a
generic renderer with omitted owners. Final actual scalar parameter names are
checked before kernel rendering. Closed scalar arithmetic checks typeOf=F64;
the IRBinOp fifth field is SrcLoc, not a separately stored result type.
Callable arrows, parameters, return types and all reachable bodies are checked;
unknown effect summaries alone do not license anything.

The only substituted instruction is the exact outer row peel:

    win + size_t(int64(global_row) - int64(__w_lo)) * columns

The original inner scalar peel remains. N/M/chunk products and bytes use bigint
and conservatively fit Int32.MaxValue, including padded chunks/strides and the
retained output skeleton on supported 32/64-bit targets. Positive run intervals
must exactly match the existing physical K grid. Every executed row obeys
lo<=i<hi and every column 0<=j<M, so its address lies inside W*M cells,
W=min(N,K). No pointer-before-allocation alias is formed. Owner checks require
the exact output name, ordered deduplicated sources, canonical windows and W.

Repeated A,A certifies both positional pairs, then uses the existing identical
declaration dedup; one source read/window feeds both scalar parameters. Distinct
source IDs of the same physical variable retain two ordered reads/owners.

Zarr.load does reread compile-time metadata during preflight. This adds compile
I/O and is not immutable-source/source-version evidence. Exceptions/nonadmission
keep the existing provider path before any omission. GenStreamRows and its
normal None-validation core are unchanged.

## Preserved semantics and limits

Provider calls, chunk assembly, normal scans and source/window failure priority
are unchanged. Each window still has its existing allocation/deletion; the
required output and its owned row skeleton remain. I1 streamed-value sentinel
and CodeGen.fs formal-capture mask semantics remain active. Unsupported raw
global stream reads/escapes still settle BL7004; ordinary optimization
nonadmission remains a safe table-backed fallback.

Targeted saving is only S*N input pointers and the private fixed extent tables.
N5/M3/K2 with two sources removes80 heap bytes on a64-bit host. No measured
performance/peak/RSS claim. ExplicitRuns and compiler boundary work remain
O(ceil(N/K)); provider metadata/storeFiles, decode buffers, fiber prologue
buffers, required output and other owners remain separate obligations.
There is no new cache, prefetch, cursor, source replay or changed FP license.
OOM identity is not promised when an allocation is removed.

## Authored focused checks (all UNRUN)

The inherited I1 values, BL7004 gather/escape/indirect controls, legitimate read
and fiber/array-output controls, finite fill and six ordered provider faults
remain. Positive/terminal/helper/fill/fault CPP must actually contain local row
loads with no input tables/aliases/extents, unchanged windows/scans/run tables,
scalar loads and required output. Independent15-cell map and term3945 pins stay.

Independent mixed/read, literal output, resident helper, scalar capture,
static metadata and unary cases require table allocation, live installation,
Array alias, actual A.data row peel and deletion. Fiber keeps its reader.
Repeated A,A pins one row/scalar declaration/read; distinct IDs pin two reads.
N1/K2 pins one-row capacity; N7/K3 pins [0,3),[3,6),[6,7) and all21 values.
Finite input overflow is checked as computed positive infinity, never BL8015.

Direct certificate tests cover stale kernel/bindings/inner/name/callee/fiber
context, owner name/order/capacity, malformed runs, hidden loop operand,
foreign equal-extent axes, scalar/array/result types and output layout.
Oversized shape and padded chunk byte tests are metadata-only: no huge payload
or resident execution. The temporary chunk-metadata edit is restored in finally.
Normal tests pin/restore all optimizer gates, FP/OMP/print/tile/timing state.

## Sole-Luna request after root review

Verify the separate I2 manifest and capture HEAD/diff, tool/environment/CWD,
fresh DLL hash/MVID and private TEMP/TMP with the established reliable wrapper.
Fresh Release build, then ONLY this focused bridge; stop first unexpected
failure. Known g++ host expects zero failures and zero skips. No automatic
standalone Zarr or full suite; root owns later integration/final-suite dispatch.

    dotnet build Blade.fsproj -c Release

    $i2Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
    $i2Dll = Join-Path $i2Checkout 'bin\Release\net10.0\Blade.dll'
    $i2Bridge = Join-Path $i2Checkout 'bench\notes\round4\D09-I2-runner-inputs\run-focused.fsx'
    $i2Cases = Join-Path $i2Checkout 'bench\.build\d9i2'
    & dotnet fsi ("--reference:" + $i2Dll) --exec $i2Bridge $i2Cases

Use a new short ASCII case path (gate limit160); record executable paths below240.
At most12 padded chunks/store,32 bytes each except N7/K3's48-byte chunks.
Return raw build/focused stdout/stderr/provenance and retained case tree.
Root should inspect positive/repeated/terminal local loads, independent fallback
tables, negative directives and actual numerical/fault evidence before acceptance.
