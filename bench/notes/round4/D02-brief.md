# D02: one required resident provider destination

State: source census returned; implementation not yet admitted.
Owner: Sol/high D, after D01 acceptance. Luna is the sole executor.

PM43 concerns a required resident output. Do not relabel D01's streamed block
workspace as this card. Begin with ordinary rank-one dense Zarr `.read` and
inspect `ZarrProvider.genReadVar`: it currently calls `genAssembleFlat`, then
allocates the final Array, copies each flat value and deletes the flat buffer.
Confirm that actual generated code still contains both full payloads.

Investigate directing chunk assembly/conversion into an already allocated final
rank-one destination, preserving the existing decoder/chunk workspace. Prefer
reusing the current assembly core with an explicit destination contract; do not
transfer a `new[]` pointer into a pool allocator with incompatible cleanup.
Rank two and higher, packed layouts, windows, MPI, providers other than Zarr,
compressed decoding and streamed consumers remain separate census results.

The source must retain its original validation, missing/fill semantics, chunk
traversal, conversion/endianness behavior, short-read precedence and publication
point. Check ownership registration and cleanup on every supported path. A
required resident destination stays live; only the duplicate full assembly
payload may disappear. Model chunk scratch separately from that output, with
checked extents and padded edge chunks. Do not claim a process-memory bound.

Return a source census, one exact baseline/emission witness, positive dtype and
tail cases the existing reader supports, explicit fallback cases, paired failure
tests, and proposed owned files. Then stop for root review before implementing.
All builds, emissions and runtime comparisons belong to Luna. The full suite
remains reserved for round closure.

## Returned census and initial review

D read the already-tested D01 materialized reference artifact
`d-streaming/generated_cpp_tests/zarr_join_v2_n11_read.cpp`, SHA256
`FA2E4523BAF0BBDFB18F3873E5240D6143C66AC1EBB55499FA678A517405D43A`.
It allocates the 11-cell flat buffer and four-cell disk chunk, releases the
chunk before missing validation, then allocates/copies the final 11-cell array.
Baseline payload peak is `max(N*E + C*D, 2*N*E)`, not three coexisting buffers.
The direct-destination target is `N*E + C*D`; when the chunk dominates N,
removing the copy need not reduce the payload peak. No measurement claim.

Root independently read `genReadVar` and `genAssembleFlatCore`. A destination
setup/store contract on the existing assembly core can preserve chunk traversal,
casts, padded tails, fill and missing diagnostics while leaving owned-flat,
phased and Icechunk wrappers unchanged. Publish the final Array wrapper only
after validation. Keep the metadata probe before allocating its pending backing.

Admission must prove a supported module-level binding; a generic provider API
does not have scope/cleanup evidence. D found no `registerPoolAlloc` call in
the ordinary provider handler. Do not expand scoped reads under this card.
Investigate an explicit codegen context/capability hook for the top-level path;
neither indentation, empty parameter lists nor variable names prove scope.
Use the existing allocator and cleanup contract, never raw-buffer adoption.

Requested first witnesses: required printed rank-one `.read`, N=11/C=4;
Float64, Float32 and Int32-to-Int64 conversion; finite missing-chunk fill;
zero extent; odd padded tail. Preserve later short/null-fill-chunk precedence
over earlier missing values and whole-read missing counts before publication.
Document allocator-failure timing separately from source data diagnostics;
the optimization changes allocation strategy and does not promise identical
out-of-memory behavior. All fatal reader paths retain current process-exit
semantics, without a new exception-cleanup guarantee.
