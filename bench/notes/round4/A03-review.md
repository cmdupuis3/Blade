# A03 design review

Root reviewed A03-census.md, SHA256
1E754EE59AC6A1CE9A77A31F6C24C6FC3E7120DCFFCB42F2D508B565B9AA1BCB,
against the accepted A02 emitted evidence. Design approved for the bounded
ordinary outlined array-return consumer, after C02-L1 is accepted and combined.
Implementation remains pending; this is not acceptance of a storage win.

The first positive is A02/030: reuse its already-planned dead local p backing
for the final return map after q is fully stored. Root requires the proposed
numeric callable/donor identity, exact current function scope ownership, one
tracking record, independent completed input backing and closed scalar kernel
proofs. A different SSA pointer alone does not prove independent storage.
Deferred inputs must decline without speculative forcing or duplicate rendering.
Caller-owned, released, wrong-scope, event-bearing and unsupported layout paths
remain fresh-allocation fallbacks. Backend admission must be reported separately
from the optimizer's shared plan.

The existing dense fill may be factored without changing arithmetic, traversal,
forcing or cleanup. Existing popPoolScope must spare the returned donor and free
q exactly once. No applyToArr or C02-L1 boundary changes are authorized by this
design. Actual emitted allocation/slot/free/return dataflow and targeted native,
interpreter and sanitizer evidence are required before integration.

This revises the initial A03 last-use-release pilot into the more precisely
evidenced LLVM return-donor gap. General let/tuple reuse, tuple-call cleanup and
C++ alias-copy retention remain separate work; none is declared complete here.
A02's 24 all-output native controls now pass. Its proposed broad category and
sanitizer follow-up is consolidated into this card's ownership implementation
validation; those unrun gates must not be represented as A02 passes.
