# B05 design disposition

Root read the full census and verified its SHA256
`EDF4D41477D9414EBE952040800BEF06BA6D3C162EDB756FB545E5C66910A418`.
The saved repro C++ and LLVM source artifacts contain their completed checksum
graphs before the original stop call. B04's native review shows that source order
does not keep the final arithmetic inside that interval.

Accept the design checkpoint. A separate native task object called by an external
driver provides a suitable first benchmark-only experiment: payload allocation,
generation, required stores and checksum completion inside the opaque call; full
result observation and allocator-matched cleanup after return and stop. Require
native non-LTO objects, conservative ABI effects, exact artifact identities and
actual machine-code enclosure evidence. This establishes compiler ordering, not
DRAM completion or zero boundary overhead.

The proposed exact-artifact adapter is preferable as an initial diagnostic to a
new general compiler export ABI. It must remain explicitly adapted output, reject
unrecognized structures/hashes, preserve the arithmetic/failure graph and never
stand in for an ordinary emitted-Blade benchmark. Frozen generated inputs must be
available reproducibly if implementation is later dispatched; ignored scratch
artifacts alone are not a distributable experiment.

No implementation is dispatched at this checkpoint. B04 remains accepted for
numeric/store/observation diagnostics and ineligible for performance claims.
Production timing/harness behavior is unchanged. The native boundary experiment
and any maintained emitter integration remain separately gated work.
