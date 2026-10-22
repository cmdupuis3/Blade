# C02 orchestrator review

2026-10-22. Reviewed the complete Optimize.fs pass, unit witnesses, six corpus
fixtures and handoff on census checkpoint `c764b6dd`. Frozen handoff SHA256
`D107518B2649E51ECE2EA465A958E9B556AD182293C4480DFD7C2CA665F41884`.
Disposition: ready for sole-runner validation; not yet accepted as tested.

The initial runner verified all eight file hashes, then the Release build failed
on three FS0001 tuple-shape errors in the hidden-payload test-case list. No test
category ran. Root confirmed the list's inconsistent statement separators and
returned it for a test-only syntax correction with all witnesses preserved.
The production rewrite remains unvalidated; see [build evidence](C02-tests.md).

The census established a specific missing route: one scalar map over a stored
matrix materializes a private matrix before partial reduction. The equivalent
explicit rowwise form already streams through IRReduceCompute in both emitters.
The rewrite uses that existing representation, retains the input and output,
constructs the concrete column axis from the source, and preserves the exact
consumer output type. It does not duplicate the existing two-vector outer
product rewrite or add backend-specific operations.

Admission requires a literal Float64 seed, reproducible serial policy, ordinary
static plain geometry, an immediate sole consumer and closed pure/total map and
fold call graphs. This permits moving evaluation into each row without moving
source-visible effects or failures. Named demanded producers retain storage.
The original fold argument order, seed and output row identity remain intact.

Review corrections incorporated before freeze:

- Subtract the matched expression only from the actual host ID. A second
  binding sharing that expression object remains a demand.
- Certify omitted Apply/MethodFor metadata explicitly, including carried axes,
  element types, roles, source IDs and structure state. Do not assume an
  expression-child walk covers metadata.
- Refuse additional host execution payloads even when they contain no reference
  to the private producer; rewriting the value alone cannot establish ownership
  of a side-table operation.

The witnesses check exact output/concrete-column metadata, actual C++ and LLVM
allocation differences, interpreter ON/OFF values, noncommutative order, empty
axes, cancellation, signed-zero seed and hidden demands. A paired-failure test
requires the later producer failure to precede an otherwise earlier row-fold
failure; the rewrite must decline. Source pins were derived independently.

Luna must verify the frozen hashes, build, run optimize and sql-reduce regression
blocks, and execute the six fixtures through both actual backends with fusion
ON/OFF. Successful default-policy category runs alone cannot prove this opt-in
route, so explicit repro emission/plan and runtime comparisons are required.
Record unsupported paths and internal skips, not just footer totals.

The modeled saving is one private mapped `8*R*C` payload and row-pointer
metadata. Required M and the row result remain. Peak process memory, bandwidth
and timing are unmeasured. Combined checks with A01 remain pending; the new
pass runs before the later lifetime/scheduling planners.
