# D02 orchestrator review

2026-10-22. Reviewed the five-file source/test diff on D01 base `9b36f1d1`,
with frozen hashes recorded in D02-handoff.md. Disposition: accepted after
targeted validation. Source `ec75ae38`, integrated as `e43d14d3`.

The route removes the flat staging allocation and final full-array copy from
one required, ordinary rank-one Zarr output. It allocates the normal final
scalar backing at the former staging site and directs the existing assembly
traversal into it. Array publication follows chunk release and the complete
missing-value report. The output remains required; this is separate from
D01's scalar terminals and bounded streaming workspace.

Admission is explicit: genModuleBinding grants the current binding ID in the
immutable context and restores the incoming capability afterward. A nested
binding, default context, wrong ID or MPI program cannot use that authority.
The generator checks the provider, identity codec, plain static rank-one
shape, element type and Int32-safe destination/chunk arithmetic. Existing
stream, packed, tiled, window, phased and generic readers keep their routes.

The shared assembly retains its fetch order, padded chunk reads, conversion,
fill, clipping and tally. A later truncated or missing/null-fill chunk still
wins over an earlier nonfinite tally. Allocation failure timing can change;
the optimization does not promise identical out-of-memory behavior. The
pending allocation uses the existing scalar allocator, with no adoption of
new[] storage and no claim of newly supported scoped cleanup.

The authored tests pair the actual admitted whole-program reader with the
unchanged generic reader in otherwise identical generated C++. They assert
exact span replacement, independently known arrays, publication ordering,
absence of staging/copy, and explicit fallback provenance. Tests cover zero
extent, odd tails, Float32, Float64, integer widening, fill and failure order.
Luna verified all five hashes and built successfully (four NU1900 warnings,
zero errors). Standalone Zarr passed 510 checks with no failures. Root's log
inspection found one internal NetCDF differential skip, despite the footer
omitting a skip total; the native library was unavailable. Route assertions,
known arrays and failure pairs all passed. See [results](D02-tests.md).

The storage model is old `max(N*E + C*D, 2*N*E)` versus new `N*E + C*D`,
excluding fixed descriptors and the existing empty-array sentinel. This
removes one allocation and N-cell copy; it does not establish a measured
RSS, bandwidth or timing improvement, and chunk-dominated peaks may be equal.
For N=0, account for the allocator's one-cell sentinel separately: it overlaps
chunk scratch in the direct route, whereas the old final sentinel was allocated
after scratch release. The ordinary positive-N saving formula is not an
empty-array peak-memory improvement claim.

Combined integration checks remain necessary after A01/C01/D01 changes are
assembled. No full-suite launch is authorized at this checkpoint.
