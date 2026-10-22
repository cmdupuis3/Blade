# D04 design disposition

2026-10-22. Root reviewed D04-design.md, SHA256
`ABCBE0C60967267B9FA5B3CB281DCE7089981CE52C2539BC02FD3C9F9BCCBD95`,
and the affected CodeGenCuda/IRAccess source. Approved for a focused safety
implementation checkpoint; no implementation or test acceptance yet.

The existing rank-one segment reader forms `win - __w_lo`, a pointer before
its allocation, then relies on a global subscript to reach the window. The
fix must instead keep the real backing pointer and subtract the window origin
from each admitted index. Both halo index expressions and ordinary elementwise
scalar operand peels use the old alias, so both belong to this one repair.

The proposed per-emission certificate identifies exact source/read sites and
proves their local coordinates fall in the filled window. A complete remaining
use census must reject opaque captures, aliases or unsupported renderers.
Global coordinates and diagnostics keep their original meaning. Unsupported
rank-one forms must fail deliberately before rendering; an unsafe old alias
is not an acceptable fallback. Rank-two row-table and resident routes remain
separate, with explicit regression witnesses.

The fix preserves provider calls, traversal, per-window validation and failure
precedence, output allocation, run table and scratch ownership. No immutable
source assumption is needed because no fetch is skipped. No chunk cache,
prefetch, bounded cursor, retention policy or provider ABI change is authorized.

Root requires checked geometry before narrowing, exact-read/local-index emission
assertions, independently derived values, global-coordinate and dtype controls,
paired window failures, and actual refusals for incomplete certificates. A01's
separate lifetime edits in CodeGenLoopNest need careful integration review.
Luna alone executes the frozen implementation after root reads its actual diff.

Implementation review in progress: root read both production diffs and the authored
Zarr witnesses. The segment caller certifies exact rank-one source identities,
Int64 offsets and checked window/output capacities. The nest accepts that immutable
certificate only for the same kernel and binding records. Halo expression substitutions
and scalar operand peels use the original window backing with a local offset; rank-one
Array aliases are removed. The rank-two row-table route remains separate.

Root independently checked the odd-tail square-data, global-coordinate and finite-fill
pins, and reviewed same-window versus future-window error precedence. Requested
additional controls cover a named capture-free scalar helper and stale certificates
after kernel/binding changes. Explicit list separators avoid the test-setup parsing
failure encountered in C02. Final frozen hashes and sole-runner gates remain pending;
this paragraph is source review, not execution evidence.

Final initial freeze reviewed: handoff
`942371023B726ED8819B1308C7FA4932E725EBD408729B019AA4FFF9954CE136`.
The implementation additionally checks callee extent payloads and requires slab
mode in the private renderer. Source capture mutability descriptors are allowed
only because the complete body grammar consumes every source occurrence as a
certified read. Root approved the Release build and standalone Zarr category,
from the isolated checkout root with absolute private artifacts.

That build stopped on offside indentation in the synthetic provider-extent test;
no Zarr tests ran. A test-only repair is requested, preserving production hashes.
The exact eight parser diagnostics are recorded in [D04-tests.md](D04-tests.md).

## Accepted checkpoint

The final handoff `9883FE0FCE6512ACA87BA319EACBF75659F76BD501A72C72C6394B2396B90C6D`
built successfully. All 551 standalone Zarr checks passed, including local addressing,
odd tails, global coordinates, dtype and fill controls, failure order and stale-certificate
refusals. The optional NetCDF comparison was the only internal skip. Earlier test-setup
build failures remain in D04-tests.md; neither changed production code.

Root read the complete targeted log and verified the final hashes. Accepted source
`fcd135f4` is integrated as `f8db0d1f`. The merged LoopNest differs from that source only
by A01's expected inline-pool cleanup addition; combined integration checks remain pending.
This repairs safe addressing without skipping reads or claiming a cache/memory saving.