# D08 census results

Frozen integration source: 5e208a77d74bccf40d0fcff5179e3af4ad9161f8 (clean before execution).
Fresh Release build passed: 0 errors, 4 NU1900 network-source warnings. The built Blade.dll SHA256 is FE12B5CEC4CAFC311D393160835278EDC93828AFEDC2368F820F5CA0CCE172AD; Blade.exe is 0DDBCD506D5ACB9A97CCBF1D0E7C6BB28F5946758572DD2DCFDFD1D879E10AD6. Tool: .NET SDK 10.0.400, C:\Program Files\dotnet\dotnet.exe SHA256 AB1B71FD3DD71062E074C9FAB8312081A81B7F2B3E0327C48C4D249C8D1A3135.

The D08 bundle, review, and checksum manifest pins were verified before execution (8 checksum entries). The build and the single FSI census completed serially with exit 0. The exact command, environment, cwd, tool identity, complete stdout/stderr, and exit are in [runner artifacts](../../.build/C04-D08-runner/). FSI lowered, validated, and emitted ordinary C++ once against a newly created private case directory. No generated program was compiled or run.

The source exposes one output, result. Its binding graph contains a materialized rank-one producer p, a seeded reduction t over p, m=t/5, and a deferred seeded reduction for result over the same p. The early scan/fold demand prevents the cell-epilogue decision; the emitted C++ retains one 40-byte p payload, a 16-byte caller window, and a 16-byte per-window decode vector. The three windows are [0,2), [2,4), and [4,5). Each window’s missing-value validation occurs before its producer arithmetic. The first fold consumes all five p elements; the second deferred fold reads them again. These are source/emission observations, not runtime or performance measurements.

The FSI callable summary reports Effects.Unknown=true; a structural totality proof remains necessary before treating the bounded-row argument as established. The capture for m is flagged mutable by the summary, though the exact source contains no assignment to it. Hand-derived values (not executed) are p=[2,5,10,17,26], t=65, m=13, result=382.

Key artifact identities: source 01-statistic-barrier.blade SHA256 E8B5E39D6326141359D218650CAD9C3B6B1B0F7F0659A9878A5A72625BE64014; emitted baseline.cpp SHA256 38D872B37EB1019A37227E11B1EAB91FD009412DF5387B0E2FC48AE6B043C1EC. Fixture metadata and chunk hashes are recorded in the census provenance and output directory. Overall: 1/1 FSI invocation passed; no skips. No full suite, generated program execution, or performance claim.

