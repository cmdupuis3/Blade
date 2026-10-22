# D08 focused validation and read-only admission diagnosis

**Current status:** the repaired D08 source checkpoint `717790c4` passed a fresh Release build and the focused private-spool gate: 82 passed, 0 failed, 0 skipped. All bounded native cases and retirement checks passed. The earlier `12fd0938` output-format mismatch and prior build/admission failures remain historical; no broad Zarr category or full suite was run.


## Compatibility checkpoint and focused retry on frozen `12fd0938`

All 20 entries in `D08-source-hashes.txt` matched; that manifest file SHA256 is `B65A052A481A5B461809317FC9AAD2E00FCE2971E5B4B5FDFF92758EAD3F4BE7`, and the handoff SHA256 is `573A8869F48CA477839742449B8A6C001E5E858BA419DE94888BA5D7A1B03604`. Checkout HEAD was `12fd093827da36f6be32fd7bb93fa42cdd24fc0a`. The unchanged D06 protocol guard is SHA256 `A63006BB84800548E3D91242695C78C263AC18039F5D7E8A0A9447428B0BD308`; harness source `src/cpp/private_spool_tests.cpp` is `F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB`.

A fresh `dotnet build Blade.fsproj -c Release` passed with 4 NU1900 warnings and 0 errors. Fresh `Blade.dll` SHA256 is `EEF2B2037291BB4637580E76A74D4551A360E89C255380F527072307A0A7BF23`; `Blade.exe` SHA256 is `00276F540D7BDF7515CBA0471F9CFECA2FAB0B6DE8EA289DEE8266D527F9DA6E`. Build stdout SHA256 is `702C55970D30002B96566CC2F0BDBDA4A6F45F83A0EF136BFBB8104266C93122`; stderr is empty.

The four authored header-order fixtures compiled as separate C++17 objects using ucrt64 g++ 16.2.0 (`58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`), `-O2 -Wall -Wextra`, with the two include orders under default macros and `-DNO_STRICT`. All four exits were 0 with empty stdout/stderr. The record-first objects hash to `0F5375E148AF53EA546B8A11EE2DBD4609110D1FD9E9AD7A823ECB4058158D52`; spool-first objects hash to `2B87F6B6D2E4BA32A9BB042BF78F8E0645076787E3CDE52BC471D8EAB4EAA4A0` (the paired default/NO_STRICT fixture objects are byte-identical).

The unchanged D06 protocol harness was compiled once under C++17 with the handoff flags and ran once against a new short ASCII scratch root. Compilation and execution both exited 0. It reported **256 passed, 0 failed, 1 skipped**; the sole expected skip was `directory symlink creation unavailable; reparse root live probe`. The scratch root was empty after completion. Harness executable SHA256 is `BF2CE0C652C2EFBC5E0F139CA109648AC79EDC8715CE018348EAEBFA3912C255`; run stdout SHA256 `8A2A86706BC775A2400854BA5AC62F9179C87781773865FD16BB098F49A9DDAE`; stderr is empty. Compile/run provenance and complete logs are in `d-streaming/bench/.build/d8compat/protocol-{compile,run}/`.

The focused bridge SHA256 is `180A8B73587C1EB1637064F470A0B376E31967310AEAA844F3FA6BA8D85F81C6`. It confirmed all registered runtime headers from the fresh build, passed the three static admission/source-order checks, and compiled/executed only `D08_n5` before the first verdict stopped the gate. The generated native program exited 0 and printed `result = 382.0`; its counters were `payload_written=40`, `payload_read=40`, `logical_disk_high_water=496`, `aggregate_tracked_capacity=6176`, and `reserved_disk=0`. The exact test matcher in `tests/PrivateSpoolTests.fs` requires `resultLine output = Some "result = 382"`, so this arm failed because the display text includes `.0`. This is the observed first test verdict; later cases were not attempted.

The case's `.cpp` SHA256 is `84495B149263FD80E99393D90B52FB753A49DE838235D1172BE049ACA9A8D757`, executable SHA256 `58BC37B1709CEDAF4E253E0B0296CFD751D5B328BE69D1D3EAF43D7ABBD7C730`, combined program output SHA256 `917FC40E95448D95DCF10FD9FF345F14196916FCC5000BF9C911531155B617A5`, and exit record is `0`. FSI stdout SHA256 `D55773D6C26C52B525D69B0FCC3A65F332828833B9A1908BE9813466180BF721`; stderr is empty; provenance SHA256 `5AF82413C815835816789C28E2F43874F916F70C26A88E7935B87BB87F1BD9C2`. The D08 owned spool directory is empty. Generated program, input fixture, copied headers and logs remain under `d-streaming/bench/.build/d8compat/cases-12fd/` for review.

This compatibility checkpoint records 4/4 header objects and the D06 protocol control passing, followed by 3 static focused checks and one first-arm native verdict failure. No broader Zarr category, performance test, or full-suite launch occurred. The result is not D08 acceptance; the value matcher requires source/root review before any retry.
## Focused retry on frozen source `427a8903`

All 16 entries in `D08-source-hashes.txt` matched. The source-hash file SHA256 is `C9BE9C3FEECA47C8EA2A640BB135658951E719B11965E56BEA1E016B48F46A7F`; handoff SHA256 `752650284EA039AC7D50F95D2BC222EC1790B3F9CEB3085929317835661C80F5`; focused bridge SHA256 `B6D4001E3628DACE6C822A5DA09323AF8D482A1E564FC4EC50566A779E8F5738`. The clean checkout was at `427a890396c90ce2b134816aacd0eebe2bc583fb`.

A fresh `dotnet build Blade.fsproj -c Release` from the checkout root exited 0 with four NU1900 feed warnings and no errors. It used .NET SDK 10.0.400 (`dotnet.exe` SHA256 `AB1B71FD3DD71062E074C9FAB8312081A81B7F2B3E0327C48C4D249C8D1A3135`) and ucrt64 g++ 16.2.0 (`g++.exe` SHA256 `58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`). Build stdout SHA256 `70B3E2DF7B193CFF8124A2A81684028DACFA9C6CF273C3966C5C35318A840280`; stderr was empty. `Blade.dll` SHA256 is `0715071C700C7F9EB15092C6C699898DED9E0239CF84F0C2C51E95086DC29E8D`; `Blade.exe` SHA256 is `2EA276172AB657269548D017C87DF75F9F2A662147142BF2B5F0019991D2BF64`.

The exact focused command was `dotnet fsi --reference:<fresh Blade.dll> --exec <run-focused.fsx> <private-case-root>`, from the checkout root. `TEMP` and `TMP` were set to the private runner directory; `BLADE_PRINT` was unset. The case root was new, 102 ASCII characters, and all bytes were ASCII. The FSI bridge confirmed the loaded DLL and every registered runtime header, including the accepted D06 private-spool and runtime-header hashes. It passed the three admission/source-order assertions, then the first `D08_n5` native compilation failed. Totals were 3 passed, 1 failed, 0 skipped.

The emitted C++ SHA256 is `653A7E027B601E6337AFF1DF083301D03F954471FAE13F80FFCAD5302BAFFC46`. The exact compiler invocation used `g++ -std=c++17 -O3 -march=native -ffp-contract=off -fno-math-errno -fwrapv ... -fopenmp -Werror=float-conversion -Werror=narrowing`. The compile log SHA256 is `768BBEE840F80914B4583B29E3D515EE0C36A84B7B3F722434C8FBE6070BCFF9`.

The compiler reported conflicting `GetModuleFileNameA` declarations between `blade_run_record.hpp` and ucrt64 `windows.h`, and `std::endian` unavailable in `blade_private_spool.hpp` under the emitted C++17 mode. The generated executable was not produced or run. The `owned` spool directory is empty; the retained `input_n5` fixture and generated source/header snapshots remain under `d-streaming/bench/.build/D08-runner-427a8903/c`. Full focused stdout SHA256 is `1BA3819B0B468EDDD9E3CF84A896BDDAC85F7AFB5D98E34A3BAB44EB2DD02552`; stderr is empty; provenance SHA256 `76FC28574E8629FF50202296C6807476EF0E7F57860E44DC67CD6A3D71CE371A`. Build/focused logs and provenance are in `d-streaming/bench/.build/D08-runner-427a8903/`. No source was changed and no further D08 cases or broader categories ran.


## Retry on repaired source freeze `383bbd20`

The corrected 14-entry manifest matched (SHA256 `433736A7553DFAA62C41F81B372AA69995F5DE4B02DB9DCBEEC8B037B743A3EB`), and the handoff and focused script hashes matched `28E8E0F6AFC65A906B20047F1721940C5B88028BE03D47CE041FEE331D691878` and `73161BE0A74888A91477DA54104859A0D3993C3BAD2B58F0CD8DBD708B320E30`. The two syntax repairs were test-only. The checkout had no tracked changes.

The second Release build passed with 4 NU1900 feed warnings and 0 errors using .NET SDK 10.0.400 and ucrt64 g++ 16.2.0. Full build output and provenance are in `d-streaming/bench/.build/D08-implementation-runner/attempt2/build.stdout.log` and `build.provenance.txt`; build stdout SHA256 `4488D3E4070A1595DCE0D8E16CC61BBC484D95DD13D38E578806DB2C293CA1CF`. Focused FSI then exited 1 before launching a native case. Its first admission case, “D08 actually replaces the resident p pool and static run table,” reported `D08 expected route was declined; stop before native cases` from `PrivateSpoolTests.fs` lines 139 and 128. Totals were 0 passed, 1 failed, 0 skipped. Focused stdout SHA256 `C7B2E4B0456E8E024E3D8493016A5D1B97FEF5ADCEF9DC7BC4960A35CF98FB0F`; stderr was empty (SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`). Provenance SHA256 `D4979A11851354095D2B404B6F2465737308C9B45558AD652569C20B2D7784B4`.

The fresh case root contains only the `input_n5` fixture store and an empty `owned` directory; no generated program or native run artifacts were created. The built DLL SHA256 is `E27D59F80593A9E8DA752D8A2F4793C6FC72D7D1FA4963CD2100DF3D052BFE88`; `Blade.exe` SHA256 is `99CD2538A2CB24A3166EC9BE64B5A334A38D37E28C815D6FD54CA45D54B07DE7`. No source edits or other gates were performed. The earlier syntax-failure attempt remains historical in attempt 1 above.

## Read-only admission diagnostic

Using that unchanged DLL and the preserved attempt2 fixture, the authorized FSI diagnostic exited 0. It recorded the planner's generic refusal at lower, resident-emission, and options-emission boundaries: `requires exactly one adjacent rank-one Float64 producer, seeded sum, extent barrier and seeded transformed sum`. It also saved the complete lower decisions, ownership tables, typed binding/callable/type/root data, and both generated C++ bodies. The full captured stdout (also containing `admission.txt`) SHA256 is `5498BAA74A8F961874647B3F33AE41BCAE3AA29567D678A81B78F5BEF857C5AB`; stderr is empty; provenance SHA256 is `E24836B3EFC0B7172D622836EF4A60EC0DA04723EBDDBFC883FE47372C2C3E35`. The diagnostic script SHA256 is `156333C8B165BA3266B0D10C57450D1C4D1AE5489151C858FD540B0FA60CA81D`; the DLL remained `E27D59F80593A9E8DA752D8A2F4793C6FC72D7D1FA4963CD2100DF3D052BFE88`.

Artifacts are under `d-streaming/bench/.build/d8diag/diagnostic-out`: `admission.txt` (SHA256 `5498BAA74A8F961874647B3F33AE41BCAE3AA29567D678A81B78F5BEF857C5AB`), `resident.cpp` (`0208013C2538DCFED5940809751D882F0138D32A0050C74BBB0D1CCEB11F18E2`) and `options.cpp` (`61F02A4EA04A67D1EA1A7AFC39C90C5AEA81CBBCBB91493324859B987D2413A4`). No executable was produced or run. The diagnostic also reports that the independent `cell-epilogue` rule declines because `t` reads `p` between production and the later fold; this describes the current baseline schedule and is not the cause of the new planner refusal. Root's review of the raw bindings identifies the refusal: the producer input's anonymous source axis has ID 1, while the valid fresh producer/consumer axes have IDs 5 and 12, and the admission guard incorrectly requires those role-specific axes to equal the source ID. This is a diagnosis only; no repair or runtime acceptance is claimed.
## Role-aware repair retry

The repaired freeze is clean at `d5b2599ae4450273359c455c6da6ffb22458defe`.
All 15 entries in `D08-source-hashes.txt` matched; the manifest SHA256 is
`80712A4B2614770D13C289C30364AFA02BEE7B5B5596F40A7AFAE66097E8162F`, and
the handoff SHA256 is `EC2B29E1E548502E9A88125DB4E7A3512F1F033813458F59B280E0685D563F45`.
The Release build passed with 4 NU1900 warnings and 0 errors. Build stdout is
`d-streaming/bench/.build/D08-role-aware-retry/build/stdout.log` (SHA256
`457E55B2B73866CDE9BEDE6F7DB11CEF054DC90665D6E95BD45F3F3F89515DFF`); stderr
is empty. Fresh `Blade.dll` SHA256 is
`A072EB74B14DEDB09CD7E05F8210FD36A2AEDAF5258E1EE51358C47710D205A0`, and
`Blade.exe` SHA256 is `925602DF915EDDA5CFFCD7554F83E86B44D0530A84524B7E05F34BF37427DBCA`.

The compiled `run-focused.fsx` gate exited 1 with 3 passed, 1 failed, 0 skipped.
Admission and source-scan assertions passed. It then failed before native
compilation or execution with BL7001: runtime-header lookup searched
`C:\Program Files\dotnet\sdk\10.0.400\FSharp\cpp\blade_portability.hpp`,
which was absent. This is a focused-gate setup failure, not a native D08 verdict.
Full stdout is `d-streaming/bench/.build/d8rr/focused.stdout.log` (SHA256
`B2C32A6253A1B715D6B7DD7E37FCB195B6E588D85B1A21F47DD4E57D0CB0C232`); stderr
is empty; provenance SHA256 `475F851EE802B4AEAE9368BD2D6337956BD6200181EB3BACD34821347AD2F676`.
The new 91-character ASCII case root contains only the N5 fixture and an empty
`owned` directory. No scratch remained. No retry, broader Zarr category, full
suite, or benchmark was run.

## Historical attempt 1: initial syntax build failure

## Frozen identity

The `d-streaming` checkout was clean at HEAD `e9790f5611cf0533c87792b137d128319a876792`. The verified 14-entry `D08-source-hashes.txt` manifest SHA256 is `E38B202CFCC972B267771131CD2A6B290D6E3D1A1563CB3FA5BF8D393E661359`; the handoff SHA256 is `1268100AC3688F3D4A47C8F003C5F3FF897F04488832F6D79AF4B989DEAB1337`. All manifest entries matched before build.

## Build result

Command: `dotnet build Blade.fsproj -c Release`, from the checkout root. .NET version was `10.0.400`; `dotnet.exe` SHA256 was `AB1B71FD3DD71062E074C9FAB8312081A81B7F2B3E0327C48C4D249C8D1A3135`. The ucrt64 compiler on PATH was g++ 16.2.0 at `C:\msys64\ucrt64\bin\g++.exe`, SHA256 `58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`. Private `TEMP` and `TMP` were set under `bench/.build/D08-implementation-runner/attempt1/build-tmp`.

The build exited 1 after 6.32 seconds. It reported four NU1900 package vulnerability metadata warnings and two F# FS0597 errors: `tests/PrivateSpoolTests.fs(88,16)` and `(126,20)`, both requiring parentheses around an argument expression. Full stdout is `d-streaming/bench/.build/D08-implementation-runner/attempt1/build.stdout.log`, SHA256 `AB0A000FA3C4113ACC77258DE15DA820C08C0EA344045A3E6069106A1538660B`; stderr is empty, SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`. Command and tool provenance are in `attempt1/build.provenance.txt`.

No Release binary identity is claimed because the build failed. `run-focused.fsx`, the private 16-case gate, and all native execution were not launched. No full/default suite, Zarr category, benchmark, or source edit was performed.

## Focused gate retry on repaired source `717790c4`

Verified checkout HEAD `717790c4c907684c98b3b685fadd784a6b1d7fd1`, all 20 entries in `D08-source-hashes.txt` (manifest SHA256 `2E39B072890C81DE594AD605ED5567FDB9C9A6A9A44365A40C50E37463BE0BE3`), handoff SHA256 `35FC39DA522576FCC40D71B0E21B5C7D0B23381B2A7C4D293A09FD5495574779`, and bridge `run-focused.fsx` SHA256 `180A8B73587C1EB1637064F470A0B376E31967310AEAA844F3FA6BA8D85F81C6`. The repaired fixture hash was `CE83C92C575069E5A30F5C16F919412365F7C98767193B053A867D3A61812BD4`. No source files changed during execution.

The fresh Release build passed with 0 errors and four NU1900 warnings because NuGet vulnerability metadata could not be reached. The first `Start-Process` wrapper returned without surfacing its completion output, so I checked the saved log and process state before retrying. Its complete stdout later showed Build succeeded, 0 errors, 4 warnings, and the provenance file recorded exit 0; stdout SHA256 is `764FFCEBE74A4AC740930FC16F8AEFDB3B7BF280B6337322C43050309A95AA52`. No matching build process remained before the separate captured invocation. Attempt 2 also completed with exit 0 in 51.07 seconds, confirming the build for the focused run. Its stdout SHA256 is `A6AABEFD9944E3BA1B68C51C17206B34B7202BE09C6EDEDAE2DF910D35A5D7F6` and stderr was empty. Fresh `Blade.dll` SHA256 is `4F60DD09CCEA1970B7DCAF6201C477BE73DF71268DBCE31E7DC8EBDD28B4F54D`; `Blade.exe` is `89D481D7CC75DE8F35AEFA36BF480F4DB63CD071535ABC4278475EBB786E9F47`.

The focused adapter ran once against that DLL with private TEMP/TMP under `d-streaming/.r4tmp/d8r3` and a new short ASCII case root under `d-streaming/bench/.build/d08r3/attempt2/cases`. It exited 0 and reported **82 passed, 0 failed, 0 skipped**. The gate covered 16 bounded native compile/run cases along with admission, refusal, output, protocol-failure, quota, and scratch-retirement assertions; all retained case roots were retired. No `SKIP` markers appeared. Full stdout is `d-streaming/bench/.build/d08r3/attempt2/focused.stdout.log` (SHA256 `B1B5620C2707B5818294A0AAF7EB5939CE509080335209FBE7C2CD3FAF01B56D`); stderr is empty. Provenance is in `build.provenance.txt` and `focused.provenance.txt`; per-case C++, executables, logs, headers, and native outputs are under `d-streaming/bench/.build/d08r3/attempt2/cases/`.

This completes the focused D08 checkpoint only. The standalone Zarr category remains pending root review; no benchmark or full-suite run occurred.
## Standalone Zarr category after focused review

The standalone `test zarr` category passed on frozen d-streaming source `717790c4c907684c98b3b685fadd784a6b1d7fd1` and the reviewed existing Release DLL SHA-256 `4F60DD09CCEA1970B7DCAF6201C477BE73DF71268DBCE31E7DC8EBDD28B4F54D`. Command: `dotnet bin/Release/net10.0/Blade.dll test zarr`, from the checkout root. `BLADE_PRINT` and the private-spool policy were unset; standard category gates were unset. TEMP/TMP used the new private `bench/.build/D08-zarr-runner/tmp` directory. No separate focused invocation occurred; the Zarr category includes the embedded `PrivateSpoolTests` call, which reported 82 passed/0 failed/0 skipped and ran the 16 bounded native cases.

The outer Zarr category totals were **601 passed, 0 failed**; its embedded focused gate added 82 passed/0 failed/0 skipped assertions. One internal skip appeared: the optional NetCDF stream differential, because libnetcdf could not load. The skip did not fail the category. Full stdout is [zarr.stdout.log](../../../../../../.round4-worktrees/d-streaming/bench/.build/D08-zarr-runner/zarr.stdout.log), SHA-256 `3632B64DF01E4D3CBCB2C424DA79924D9E9B98544B19EDE6C9856DB802EB16A9`; stderr contains only the benign provenance line `[provenance] folded x from tests/fixtures/zarr_stores/zarr_fold_store@3fb28b6037f7` (84 bytes), SHA-256 `2DFFC42C8783F8B9F8AB0CD9EEF29DF957BC3CBD17EE67D221320F60480B8478`. Command/environment/source/binary provenance is in [provenance.txt](../../../../../../.round4-worktrees/d-streaming/bench/.build/D08-zarr-runner/provenance.txt), SHA-256 `42B0D6991029394CB26CA0DE108326223B0817E607F6E237F3B4FF9EC2FB1CD6`.

This completes the authorized standalone Zarr regression. No full suite or benchmark ran.
