# D08 focused validation and read-only admission diagnosis

**Current status:** the repaired Release build passed. The focused gate stopped at its first positive admission check before any native case; a follow-up read-only probe captured the planner reason and emitted text. Root traced the refusal to an overstrict input/output index-role guard. The initial syntax/build failure is historical evidence below. No Zarr category or other test gate ran.


## Retry on repaired source freeze `383bbd20`

The corrected 14-entry manifest matched (SHA256 `433736A7553DFAA62C41F81B372AA69995F5DE4B02DB9DCBEEC8B037B743A3EB`), and the handoff and focused script hashes matched `28E8E0F6AFC65A906B20047F1721940C5B88028BE03D47CE041FEE331D691878` and `73161BE0A74888A91477DA54104859A0D3993C3BAD2B58F0CD8DBD708B320E30`. The two syntax repairs were test-only. The checkout had no tracked changes.

The second Release build passed with 4 NU1900 feed warnings and 0 errors using .NET SDK 10.0.400 and ucrt64 g++ 16.2.0. Full build output and provenance are in `d-streaming/bench/.build/D08-implementation-runner/attempt2/build.stdout.log` and `build.provenance.txt`; build stdout SHA256 `4488D3E4070A1595DCE0D8E16CC61BBC484D95DD13D38E578806DB2C293CA1CF`. Focused FSI then exited 1 before launching a native case. Its first admission case, “D08 actually replaces the resident p pool and static run table,” reported `D08 expected route was declined; stop before native cases` from `PrivateSpoolTests.fs` lines 139 and 128. Totals were 0 passed, 1 failed, 0 skipped. Focused stdout SHA256 `C7B2E4B0456E8E024E3D8493016A5D1B97FEF5ADCEF9DC7BC4960A35CF98FB0F`; stderr was empty (SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`). Provenance SHA256 `D4979A11851354095D2B404B6F2465737308C9B45558AD652569C20B2D7784B4`.

The fresh case root contains only the `input_n5` fixture store and an empty `owned` directory; no generated program or native run artifacts were created. The built DLL SHA256 is `E27D59F80593A9E8DA752D8A2F4793C6FC72D7D1FA4963CD2100DF3D052BFE88`; `Blade.exe` SHA256 is `99CD2538A2CB24A3166EC9BE64B5A334A38D37E28C815D6FD54CA45D54B07DE7`. No source edits or other gates were performed. The earlier syntax-failure attempt remains historical in attempt 1 above.

## Read-only admission diagnostic

Using that unchanged DLL and the preserved attempt2 fixture, the authorized FSI diagnostic exited 0. It recorded the planner's generic refusal at lower, resident-emission, and options-emission boundaries: `requires exactly one adjacent rank-one Float64 producer, seeded sum, extent barrier and seeded transformed sum`. It also saved the complete lower decisions, ownership tables, typed binding/callable/type/root data, and both generated C++ bodies. The full captured stdout (also containing `admission.txt`) SHA256 is `5498BAA74A8F961874647B3F33AE41BCAE3AA29567D678A81B78F5BEF857C5AB`; stderr is empty; provenance SHA256 is `E24836B3EFC0B7172D622836EF4A60EC0DA04723EBDDBFC883FE47372C2C3E35`. The diagnostic script SHA256 is `156333C8B165BA3266B0D10C57450D1C4D1AE5489151C858FD540B0FA60CA81D`; the DLL remained `E27D59F80593A9E8DA752D8A2F4793C6FC72D7D1FA4963CD2100DF3D052BFE88`.

Artifacts are under `d-streaming/bench/.build/d8diag/diagnostic-out`: `admission.txt` (SHA256 `5498BAA74A8F961874647B3F33AE41BCAE3AA29567D678A81B78F5BEF857C5AB`), `resident.cpp` (`0208013C2538DCFED5940809751D882F0138D32A0050C74BBB0D1CCEB11F18E2`) and `options.cpp` (`61F02A4EA04A67D1EA1A7AFC39C90C5AEA81CBBCBB91493324859B987D2413A4`). No executable was produced or run. The diagnostic also reports that the independent `cell-epilogue` rule declines because `t` reads `p` between production and the later fold; this describes the current baseline schedule and is not the cause of the new planner refusal. Root's review of the raw bindings identifies the refusal: the producer input's anonymous source axis has ID 1, while the valid fresh producer/consumer axes have IDs 5 and 12, and the admission guard incorrectly requires those role-specific axes to equal the source ID. This is a diagnosis only; no repair or runtime acceptance is claimed.
## Historical attempt 1: initial syntax build failure

## Frozen identity

The `d-streaming` checkout was clean at HEAD `e9790f5611cf0533c87792b137d128319a876792`. The verified 14-entry `D08-source-hashes.txt` manifest SHA256 is `E38B202CFCC972B267771131CD2A6B290D6E3D1A1563CB3FA5BF8D393E661359`; the handoff SHA256 is `1268100AC3688F3D4A47C8F003C5F3FF897F04488832F6D79AF4B989DEAB1337`. All manifest entries matched before build.

## Build result

Command: `dotnet build Blade.fsproj -c Release`, from the checkout root. .NET version was `10.0.400`; `dotnet.exe` SHA256 was `AB1B71FD3DD71062E074C9FAB8312081A81B7F2B3E0327C48C4D249C8D1A3135`. The ucrt64 compiler on PATH was g++ 16.2.0 at `C:\msys64\ucrt64\bin\g++.exe`, SHA256 `58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`. Private `TEMP` and `TMP` were set under `bench/.build/D08-implementation-runner/attempt1/build-tmp`.

The build exited 1 after 6.32 seconds. It reported four NU1900 package vulnerability metadata warnings and two F# FS0597 errors: `tests/PrivateSpoolTests.fs(88,16)` and `(126,20)`, both requiring parentheses around an argument expression. Full stdout is `d-streaming/bench/.build/D08-implementation-runner/attempt1/build.stdout.log`, SHA256 `AB0A000FA3C4113ACC77258DE15DA820C08C0EA344045A3E6069106A1538660B`; stderr is empty, SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`. Command and tool provenance are in `attempt1/build.provenance.txt`.

No Release binary identity is claimed because the build failed. `run-focused.fsx`, the private 16-case gate, and all native execution were not launched. No full/default suite, Zarr category, benchmark, or source edit was performed.
