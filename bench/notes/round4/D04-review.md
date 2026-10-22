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
