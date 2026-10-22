# B07 root native review

Root reviewed the exact eight B07-runner-a2 native artifacts and their saved
format/section/disassembly/command records. The supplemental B07-native-evidence.md
(SHA256659C0B00AD8E1C4B44CB308E1A832CF44806F3BC906D5201E0A182A19A735ADC)
provides per-arm executable/provenance hashes and native addresses. Root independently
checked the timer call windows, final checksum publication, full p/q store streams
including cell2052, actual observer loads/bounds, and allocator-matched cleanup.
The accompanying per-arm audit JSON binds this judgment to the exact provenance
and evidence identities. Saved-output validator execution remains Luna's next gate.

All eight objects are native x86-64 COFF, with executable PE image code. Separate
compile and link commands use no LTO; task, driver and observer remain actual native
call boundaries. Driver/observer object hashes are identical across all eight;
root read the shared observer's two paths: descriptor+0x20/+0x28 roots load at
object+0x1ac/+0x1b6 and +0x412/+0x41c, increment the index and compare with0x805.
No mismatch short-circuits either full2053-cell scan. JSON output reads those
same roots before release (shared helper+0x1a/+0x9a; end=base+0x4028).

Unlike B04, each task's final sx+sy addition and both outgoing scalar values
precede b07_publish and return. Root traced the Win64 outgoing stack+0x30/+0x38
values into descriptor+0x30/+0x38. Every main calls QPC(t0), the real task, then
QPC(t1) before observation. Independent reference arithmetic after t1 is verification,
not deferred computation of the published results. The native buffers stay live
through all five observation/corruption/restore calls and both JSON traversals.

GCC output loops cover2052 cells with vector stores and a separate2052nd-index
scalar tail. LLVM covers0..2047 in64-cell unrolled groups,2048..2051 in a vector
epilogue and2052 in a scalar store. C++ invokes the real producer helper for p
and computes/stores q in its task; the unused consumer helper is not counted.
Natural Fortran uses separate output passes; expert Fortran forwards values while
retaining both store streams. Licensed FMA/vector reductions and strict ordered
reductions remain distinct. Numeric evidence uses the declared tolerance, not a
cross-backend bit-identity claim.

Root inspected matched release: C++ typed/count-aware pool cache or delete[]
branches; LLVM four blade_free calls recovering the original aligned-allocation
header; Fortran saved-descriptor identity checks then four matching frees. Invalid
descriptor probes precede release and duplicate release is rejected. This is logical
ownership retirement, not proof of immediate process-RSS decline or zero retained
allocator capacity. No sanitizer or arbitrary-input lifetime claim follows.

Disposition: native-enclosure evidence accepted for these eight canonical diagnostic
artifacts only, subject to Luna's saved-output identity/audit validator. Do not rerun
successful native arms. Prepared mutation is a separate future bounded arm; no
performance-eligible benchmark or production-emitter behavior is admitted here.