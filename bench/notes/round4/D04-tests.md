# D04 validation results

Status: the third reviewed freeze built successfully and standalone `test zarr` passed 551 checks with zero failures. The log contains one internal NetCDF differential skip because the optional native library is unavailable; that skip is omitted from the footer. No source files were modified by the runner.

The initial handoff hash matched `942371023B726ED8819B1308C7FA4932E725EBD408729B019AA4FFF9954CE136`. The syntax-corrected handoff matched `7A697E48AA4A6F401265CE93FF62CDA753469DAB8AB95E1814702D5FABABC8E3`; the corrected `tests/ZarrTests.fs` hash is `14C85F7F91113941F62DFF1BDEB659E0C4BB6F642C625D4E44EE3BA503B4F44A`. The four source/test hashes are:

| File | SHA256 |
|---|---|
| `src/CodeGenCuda.fs` | `5C6F99CA6C29A1171991EEECBBFCE9B79C65F8CCF150F53EBF401AE8F75AA96E` |
| `src/CodeGenLoopNest.fs` | `FEDD661A83B58BEAB080CECBBE57C5E783D24BF9D1333479C45AA586AF365AF3` |
| `tests/ZarrTests.fs` | `914D35638B7F62C35631975BDF82B0C4AB8C4763F9A12E5FBF902CEFA23B79BD` |
| `src/providers/ZarrProvider.fs` (unchanged control) | `82EA96945B462D181F3B2D27261E00B66FC4BF91995DE183509AEDC377765F80` |

Command: `dotnet build C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming\Blade.fsproj -c Release`

The explicit working directory was the d-streaming checkout root. Private TEMP/TMP and all runner outputs were under `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming\bench\.build\D04-runner`. The build exited 1 after 26.71 seconds with 4 NU1900 warnings and 8 F# errors. NU1900 reports that the NuGet vulnerability feed could not be reached.

The primary diagnostics are `tests/ZarrTests.fs(1851,33)` FS0058, unexpected offside indentation from the context started at `(1850,33)`, and `(1850,82)` FS0611, missing function body. Parser fallout includes FS0010 at `(1851,33)` for `{` and `(1851,169)` for `)`, FS0604 unmatched `{` at `(1850,29)`, FS0583 unmatched `(` at `(1849,60)`, and an incomplete structured construct at `(1851,172)`.

The initial build output is [`build-94237102.log`](../../../../d-streaming/bench/.build/D04-runner/build-94237102.log), with command and provenance in [`build-94237102.provenance.txt`](../../../../d-streaming/bench/.build/D04-runner/build-94237102.provenance.txt). It failed after 26.71 seconds with 4 NU1900 warnings and 8 errors, led by FS0058 offside indentation at `ZarrTests.fs(1851,33)` and FS0611 missing function body at `(1850,82)`; the remaining parser errors are recorded in the initial build log.

The corrected snapshot's Release build failed again after 23.07 seconds with 4 NU1900 warnings and 2 errors. FS0041 at `tests/ZarrTests.fs(1681,18)` says the `File.WriteAllText` overload is ambiguous for arguments `string * 'a` (the `string` and `ReadOnlySpan<char>` overloads); FS0001 at `(1738,73)` says inferred `'a -> string` does not match `string`. Full output: [`build-7A697E48.log`](../../../../d-streaming/bench/.build/D04-runner/build-7A697E48.log); provenance: [`build-7A697E48.provenance.txt`](../../../../d-streaming/bench/.build/D04-runner/build-7A697E48.provenance.txt). No usable binary was produced by either snapshot, and the Zarr category did not launch. The expected internal NetCDF differential skip from earlier Zarr runs was therefore not observed on these attempts.

## Third freeze validation (handoff `9883FE0F…6B90C6D`)

The latest handoff and all four source/test hashes matched: `CodeGenCuda.fs` `5C6F99CA…F75AA96E`, `CodeGenLoopNest.fs` `FEDD661A…AF365AF3`, `ZarrTests.fs` `B1034A90…11BF9F5F`, and unchanged `ZarrProvider.fs` `82EA9694…7765F80`. The Release build from the isolated d-streaming checkout root succeeded with 4 NU1900 warnings and 0 errors. `Blade.exe` SHA256: `95071516FB08CDFAAD00A34E40534A6DFA5FFE01341C71905C5D6A0BBC4CC3A4`; `Blade.dll`: `F44DE8DDE4D700844CCEDFA1FA3304DAF7283211DF3045A2A3FD29ECFBC0A7E6`. Full log and provenance: [`build-9883FE0F.log`](../../../../d-streaming/bench/.build/D04-runner/build-9883FE0F.log), [`build-9883FE0F.provenance.txt`](../../../../d-streaming/bench/.build/D04-runner/build-9883FE0F.provenance.txt).

The only category command was `Blade.exe test zarr`, run from the isolated checkout root with private TEMP/TMP and outputs under `bench/.build/D04-runner`. It exited 0; footer: `Zarr Provider: 551 passed, 0 failed`. The full log contains exactly one `SKIP nc stream differential`: libnetcdf could not be loaded by g++, so the optional NetCDF differential was unavailable. This internal skip does not appear in the footer. No other `SKIP` markers were found. The new rank-one local-window assertions all passed, including per-window short/null failure order and global error locations. Full output and provenance: [`test-zarr-9883FE0F.log`](../../../../d-streaming/bench/.build/D04-runner/test-zarr-9883FE0F.log), [`test-zarr-9883FE0F.provenance.txt`](../../../../d-streaming/bench/.build/D04-runner/test-zarr-9883FE0F.provenance.txt).
