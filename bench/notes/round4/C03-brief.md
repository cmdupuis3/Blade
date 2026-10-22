# C03 scoped request: several selected seeded terminals

This is a queued source/design request, not implementation approval. Begin after
C02-L1 is accepted and the lane has the integration snapshot. Own one PM19 extension.

C01 currently admits exactly one direct seeded scalar fold over a private top-level
mask/compound pair (`Optimize.fs`, `fuseSelectedTerminalModule`). Its emitted guarded
fold scans the stored source without selection storage. The existing sibling-fold
pass already parses seeded `IRReduce` over a stored array and builds independent
joined accumulators. Determine whether extending C01 to several independently
seeded terminals can use that route without adding another backend loop mechanism.

The first useful witness has two different folds and nonzero seeds over the same
selected Float64 values. Preserve each chain's ascending association, false-predicate
identity step, empty-selection seed bits, and terminal binding dependencies. Include
noncommutative folds and cancellation so a single shared accumulator cannot pass.
Keep source, predicate and fold graph admission as narrow as C01 unless a separately
reviewed proof justifies expansion. No unseeded folds, selected maps, external input
rereads, or independently selected arrays in this card.

Audit the complete private-demand census across all terminals, types, hidden payloads,
captures, foreign bindings, aliases and printing. No intervening write can change the
source version. Preserve demanded mask/selection outputs. A terminal used as a later
seed is not independent; a falling seed/fold/predicate or intervening abort is a
separate refusal/order control. Do not exempt whole host bindings from residual demand.

Report separately (1) eliminated mask/positions/value stores, (2) source traversal
count, and (3) predicate evaluations. Two guarded scans can establish storage elision
without establishing one traversal. The existing sibling join may repeat a pure
predicate per leg; do not claim predicate sharing unless emitted dataflow proves it.

The census should identify exact pass ordering and the current sibling admission or
refusal, proposed owned files, storage model, independent value pins and smallest
positive/refusal/runtime matrix. No build/emission/execution by the coding lane;
Luna receives any necessary diagnostic request through root. No implementation before
root reviews the census. Full suite remains reserved.
