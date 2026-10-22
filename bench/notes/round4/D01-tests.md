# D01 targeted validation

**Current disposition: accepted for integration.** The corrected frozen source rebuilt and
the standalone Zarr block passed **389 passed, 0 failed** with **1 NetCDF-dependent test
skipped** because `libnetcdf` could not be loaded. The corrected test fixture hash is
`A85ED13D07EAB93FF4AFC1B50A7DFA2637A500004F42D63C63D57C0C57FA33C6`; the other four
source hashes below are unchanged. Full logs: [corrected build](../../../../d-streaming/bench/.build/D01-runner/build.log)
and [Zarr results](../../../../d-streaming/bench/.build/D01-runner/zarr.log). The initial
syntax-failure build is retained separately at
[build-syntax-failure.log](../../../../d-streaming/bench/.build/D01-runner/build-syntax-failure.log).

Runner: Luna/high. Frozen checkout `codex/round4-d-streaming`, base `361504b60a1edd8a5c06993976daae7d7d25d9d3`. All five handoff hashes matched before execution: CodeGenBinding.fs `F4A41399274E94B23F3B5924C4BAD4BA4964DC1BCA9E7E7EAF7216B22FCE98D0`, CodeGenState.fs `5FFDE77E98038C3DA4AC6E3B5D8966AC57AA2711D22EDC372362C999B01B4DDE`, CodeGen.fs `997E51D28C9EAA2BBFA8DC4AC7720DD4F7837413D5D08C6092FFD261C5A03338`, ZarrProvider.fs `76A5D488D0D73E7322E4540E48EFCE45FCE0E7279EFACD7926CB0B1127B26EC6`, and ZarrTests.fs `C369D71E97AD7F65D0AC1BD5820358C426DBC5CC5B69C4CEA0E2C1F79CDC1029`.

The first frozen snapshot's exact Release command `dotnet build Blade.fsproj -c Release`
failed with 61 F# syntax/indentation errors, first FS0588 at `tests/ZarrTests.fs(1150,9)`.
This failure was corrected in the test fixture; it is historical and superseded. The initial
complete output is retained at [build-syntax-failure.log](../../../../d-streaming/bench/.build/D01-runner/build-syntax-failure.log).

On the corrected snapshot, all four unchanged production/provider files matched their
original handoff hashes and `tests/ZarrTests.fs` matched the corrected hash above. From the
checkout root, with TEMP/TMP set to `bench/.build/D01-runner/tmp`,
`bin/Release/net10.0/Blade.exe test zarr` exited 0. The block reported 389 passed, 0 failed,
and one skip because the NetCDF-dependent case could not load `libnetcdf`. The passing
assertions cover route selection and values, provenance and refusal paths, failure ordering,
printing/mutation restrictions, LLVM provider-I/O refusal, and related Zarr behaviors.
The corrected exact Release build exited 0 with 4 NU1900 warnings (NuGet vulnerability
metadata endpoint unavailable); `Blade.dll` SHA-256 was
`4DA773EC3DCA13B962D1FF0452B37D55A83F3902F66C1210C3CA6221C43BA04E`. No source edits were
made by the runner; no full/default suite or benchmarks were run. Full-suite launch count
remains zero.
