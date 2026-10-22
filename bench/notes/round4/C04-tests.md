# C04 implementation validation checkpoints

**Status:** the reviewed I1 repair passed the Release build, optimize gate, native C++/LLVM matrix, emission/plan controls, and the three authorized sql-reduce gates. Earlier build and optimize failures and runner-only capture/path issues remain preserved below.

## Initial frozen identity (attempt 1)

The isolated checkout is `c-terminals` at HEAD `e1cca133441960261ac6162062dd40e980bad901`; it was clean before execution. All 11 files listed in `C04-handoff.md` matched their pinned SHA256 values: `src/Optimize.fs`, `src/Lowering.fs`, `tests/OptimizeTests.fs`, and corpus fixtures 040–047. The handoff file SHA256 is `8A848F67C3982A8869E4643216AEED102C33EE2F70F318654CA809CD1C487003`.

## Build result

Command: `dotnet build Blade.fsproj -c Release`, from the `c-terminals` checkout root. .NET version was `10.0.400`; `dotnet.exe` SHA256 was `AB1B71FD3DD71062E074C9FAB8312081A81B7F2B3E0327C48C4D249C8D1A3135`. Private `TEMP` and `TMP` were set to `bench/.build/C04-implementation-runner/tmp`. The runner explicitly unset `BLADE_PRINT`, `BLADE_LLVM`, `BLADE_TILE_CACHE`, and `BLADE_EXE_CACHE`; no generated program or test harness was launched.

The build exited 1 after 26.13 seconds. It reported four NU1900 warnings because package vulnerability metadata could not be fetched, then five F# errors in `tests/OptimizeTests.fs` around lines 2039–2043:

- FS0611: missing function body at `(2039,67)`.
- FS0058: offside indentation at `(2040,25)` (reported twice).
- FS0010: unexpected `if` at `(2040,25)`.
- FS0010: incomplete structured construct at `(2043,25)`.

The full stdout log is `c-terminals/bench/.build/C04-implementation-runner/build.stdout.log`, SHA256 `25A8A52A885DFF85607D5A421E36D414F984EE894B5B3B3251FCD7E933FB50F8`. The stderr log is empty (SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`). Command, checkout, tool, and runner-set environment provenance is in `c-terminals/bench/.build/C04-implementation-runner/build.provenance.txt`.

No binary identity is reported because the build failed; any pre-existing output must be treated as stale for this checkpoint. The optimize gate and all later steps remain unrun. No source files were edited. No full suite, generated native run, oracle, sanitizer, or benchmark was launched.

## Corrected freeze retry (attempt 2)

The coder's test-only layout repair was committed as HEAD `0f4b48cc11490b8738e0f990fe5f62712dde89d8`. `tests/OptimizeTests.fs` changed to SHA256 `1FD226AFAFD50799EDFA1C566D709CAFB2750742022AD0D45AFC5340A8701A21`; production and all eight corpus hashes matched the handoff. The updated handoff SHA256 is `BBDF8F4371094091C3C8B36800A13AEBD3236B832E2EE63730AE3AD08E3BCD3E`.

The same `dotnet build Blade.fsproj -c Release` command ran from the checkout root using the separate `attempt2` private temp and log directory. It exited 1 after 22.49 seconds with four NU1900 package vulnerability metadata warnings and three F# errors, all at `tests/OptimizeTests.fs(2020,150)`: FS0001 expected `Map<Types.IRId,IR.RandomFillSpec>` but found `bool` (reported twice), and FS0003 that a Map value is not callable. Full stdout: `c-terminals/bench/.build/C04-implementation-runner/attempt2/build.stdout.log`, SHA256 `B5DE208EC56F77B5C1293F5C3AB695D4D7CA1E1AF5C756D1A55FEB51336FE8FC`; stderr is empty, SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`. Provenance is `attempt2/build.provenance.txt`. `BLADE_PRINT`, `BLADE_LLVM`, `BLADE_TILE_CACHE`, and `BLADE_EXE_CACHE` were unset; the captured `BLADE_*` environment had no other entries. No Release binary identity is claimed, and no optimize gate was launched.

Root's read-only diagnosis locates the second attempt's type/application errors in the new multiline foreignModule record at lines 2020–2021. The runner performed no source edits or further inspection after the build failure; coder repair is pending review.

## Corrected freeze retry (attempt 3)

The second layout-only repair was committed at HEAD `89b49014d8baa54bbd68dd5c30dc251a312a2c99`. All 11 handoff files matched: `tests/OptimizeTests.fs` SHA256 `3C757F7DD0EB2B6A2C3178509619D1C5B35E3BBB0FC83827FC205956D5EE6837`, updated handoff SHA256 `E9DCA731C11700B69CCAC7B9D4D24956021E334AF5829770754EC39450855F31`, unchanged `src/Optimize.fs` `6263600DF6B8A1EC82225E5BFB3B456F8563F57566A0A597C414326081C92F01`, unchanged `src/Lowering.fs` `D04639FFDB34635D3D4E06295E49901547042795BA7CEE3AB7E021B739D5718E`, and corpus fixtures 040–047 matching the handoff. The checkout was clean before the attempt.

`dotnet build Blade.fsproj -c Release` passed from the checkout root in 1 minute 3.80 seconds with four NU1900 package vulnerability metadata warnings and zero errors. Build stdout is `c-terminals/bench/.build/C04-implementation-runner/attempt3/build.stdout.log`, SHA256 `4191AA1C2C351C7F07BDCF90E1933479EC85E88A2E43F22FD5C69441EB0F98AD`; stderr is empty. The resulting `Blade.exe` SHA256 is `6A1D4D0DCB884C46366392558BE7E0ACFE95E25650D442FEA0F272E2531CB1DD`; `Blade.dll` SHA256 is `580D8378F2090290D310B3618168F1B77F546B6BBCC7C4EB2811B2031CE05CF8`.

Then `bin/Release/net10.0/Blade.exe test optimize` ran from the same checkout root with private `TEMP`/`TMP` at `attempt3/optimize-tmp` and `BLADE_PRINT` unset. The captured `BLADE_*` variables were empty after explicitly unsetting `BLADE_PRINT`, `BLADE_LLVM`, `BLADE_TILE_CACHE`, and `BLADE_EXE_CACHE`; no `SKIP` marker or skipped footer count was present. Exit was 1: **691 passed, 2 failed**. The failing assertions were `transpose_rows_interp_independent_on_off_R0` and `transpose_rows_interp_independent_on_off_negative_zero`, both reporting `missing: same output/error/status`. The C04 C++/LLVM layout assertion passed (`CPP rank2=1/2/2; LLVM layouts=[6x8; 3x8]/[6x8; 6x8; 3x8]`), as did the route/census assertion and the other focused interpreter controls. Full stdout is `attempt3/optimize.stdout.log`, SHA256 `38BC185FF72CDCF7AB5517A5B241F0721FE3803D7134267BEB79B21036B5AD37`; stderr is empty, SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`. Build and optimize provenance files record commands, source/handoff hashes, tool identity, binary identity where available, cwd and private temp paths.

Per dispatch, execution stopped at the first failed category. No native C++/LLVM matrix, further category, sanitizer, full suite, oracle, or benchmark was run. No source files were edited by the runner.
## Empty-axis diagnostic adapter attempt 1

The exact existing attempt3 assembly was reused; no compiler rebuild occurred.
The approved adapter failed while serializing SrcLoc in the first R0-on IR dump,
before invoking the interpreter. Only a partial 7752-byte typed-IR file and the
source were saved; there are no raw interpreter results and the other three arms
did not run. This is a diagnostic adapter failure, not another compiler/test result.
Evidence is retained under c-terminals/bench/.build/C04-empty-diagnostic-runner;
stdout SHA F106BC9E0975B619E43D74F841DFAF3D475024394B4B7F94E081247ADE3B4002,
stderr SHA 8049B9ECE3A033746E6FBE43F2631A6C9D4C31181F4435FA7E60A50BC380A48E.
C will add explicit serialization of SrcLoc.Span and save interpreter results
before diagnostic IR serialization. No production repair is authorized yet.

## Empty-axis diagnostic retry confirmed the cause

The repaired bridge (24EBB9A29E9E5EB5399A9506805B6A17D6A53CBADACD1FA2186D6417C95B01F2)
completed all four lower/validate/interpreter arms and full typed-IR dumps with
exit0 against the same attempt3 assembly. R0-on returned [-7.25,-7.25,-7.25]
and negative_zero-on returned [-0.0,-0.0,-0.0], both status0 and empty stderr.
Both optimizer-OFF arms returned status125, empty stdout and
`interp-unsupported: transpose: dimension index out of range`.
Raw files are in c-terminals/bench/.build/C04-empty-diagnostic-runner/retry1-output.
Root read the raw results and the OFF IRArrayLit payload: its empty element list
has two plain scalar index axes with literal extents0 and3. The interpreter's
rank1 empty-literal fallback therefore loses shape already present in the IR.
The narrow interpreter representation repair is now authorized; comparison and
independent expected seeds remain unchanged. No native or category run followed.

## Reviewed C04-I1 freeze (attempt 4)

The I1 interpreter repair was committed at HEAD `1331b1786ca9aba116868f77bb9f8b4c6ae224bc`. All 14 working-file hashes in the updated handoff matched: `src/Interp/ArrayOps.fs` `83CD87F4904A3714B24B80C3D19F859C2EA4014A6E3D803E3F74377D69B76FBE`, `tests/OptimizeTests.fs` `BC5A64D421394778385E18E8583221A92453C76E86D6E49066EB9CB6CBC2C716`, unchanged `src/Optimize.fs` `6263600DF6B8A1EC82225E5BFB3B456F8563F57566A0A597C414326081C92F01`, `src/Lowering.fs` `D04639FFDB34635D3D4E06295E49901547042795BA7CEE3AB7E021B739D5718E`, the diagnostic bundle, and corpus 040–047. Handoff SHA256 is `34622693E9D17B6830C48E0AF098C2BFEE0C0A11F9A88E3B4466BDFBF59B991C`.

`dotnet build Blade.fsproj -c Release` passed in 47.08 seconds with four NU1900 warnings and zero errors. `Blade.exe` SHA256 is `4483D13813F0F66C98C0F62228066261A39D3DF28994BE3D11DBD35D215E11FE`; `Blade.dll` SHA256 is `C40A13D920068C7967DBFA2824B687C3DE7F11912D84F3957C338DA5B0FB0C23`. Full build stdout is `c-terminals/bench/.build/C04-implementation-runner/attempt4/build.stdout.log`, SHA256 `8615F9AE164C04FE84A2E882CDA79039F7EC04C683A675C6F630B3043EE50A2F`; stderr is empty.

Then `bin/Release/net10.0/Blade.exe test optimize` passed: **706 passed, 0 failed, 0 skipped**, with no internal `SKIP` markers. The 13 `transpose_rows_I1_` witnesses all passed: `empty_literal_rank2_0_3`, `empty_literal_rank3_0_3_2`, `rank1_unchanged`, `rectangular_existing_rows_unchanged`, the seven `excluded_empty_*` cases (ragged, compact, dependent, unresolved, negative, outside-managed-extent, leading-nonzero), `nonempty_ragged_route_unchanged`, and `nonempty_compact_route_unchanged`. The original exact R0 and negative-zero ON/OFF interpreter comparisons both passed, alongside ordered, C0, cancellation, stored-alias, total-block, deferred-failure and seed-failure controls. The C++/LLVM layout check passed with C++ rank-2 allocations 1/2/2 and LLVM layouts `[6x8; 3x8]` ON versus `[6x8; 6x8; 3x8]` OFF and demanded.

The full optimize stdout is `attempt4/optimize.stdout.log`, SHA256 `B6C2CF8DB60F38A9AFEE3C868B3BCE4AC96D2178264D56A8270180682A0522AC`; stderr is empty. Build and optimize provenance files record checkout, handoff and test hashes, commands, tool identity, fresh binary identity, CWD, private TEMP/TMP and effective BLADE environment.

## Native corpus matrix and emission/plan controls

On the same frozen source and Release binary, the serialized C++/direct-LLVM matrix completed all 32 unique runs for fixtures 040–047, with fusion on/off. All output, exit-status, and source-site expectations matched; direct LLVM provenance was verified for every LLVM route. The expected aborts remained at their pinned sites: 045 in `late_transpose_source` before the fold and 046 in `transpose_seed_failure`. The normalized all-output twin comparison is `c-terminals/bench/.build/C04-implementation-runner/native/matrix-normalized-comparison.txt` (SHA256 `7C0134404C9C2795510C0E34767CDC4838BDDC1E9D1CF20861A9286455B17664`); readable 32-row matrix table SHA256 `AA58EFE43738F68EC9EE93B8A39952015F7A67407CAB94BE70873776A651A439`.

The requested emit/plan batch completed all 32 invocations (four fixtures × two backends × two fusion settings × emit/plan). `transpose-row-fold` was applied only to 040 with fusion on in both backends. With fusion off, 040 retained its separately allocated 3×2 transpose; 044 retained named `T` after the mutation of `M`; 045 and 046 declined the rewrite. Saved generated sources and JSON plans, with artifact hashes and per-command outcomes, are indexed by `c-terminals/bench/.build/C04-implementation-runner/native/inspect/emit-plan-results-v3.tsv` (SHA256 `33EB764BA94813227FF19755FD9831B5E32D9907B92CB67E97634EEA30CDFD35`). The full driver stdout is `matrix-driver-v3.stdout.log` (SHA256 `AA0B8AABE45C4878F6AF1E864953EA1ABB3E4F23DD78684E7A33B718A41C7C5E`); stderr is empty. Root separately reviewed the emitted address and fold graphs. These are emission and correctness results, not a measured memory or speed claim.

Two runner-only capture issues were preserved and corrected without rerunning completed arms: the first 041 CPP wrapper did not save process output/exit after PowerShell treated native stderr as an exception; that single arm was recaptured with the reviewed redirected-process wrapper. A first 045 CPP attempt used a 263-character executable path and failed to launch despite successful emission; the one approved short-path retry passed its expected source failure. Neither failed wrapper attempt is counted as a program result.

## Authorized final categories

`Blade.exe test sql-reduce` exited 0: 47 passed, 0 failed, 0 skipped. This includes 41 full compile/run/value checks, two expected lowering rejections, and four expected abort cases. Full stdout is `c-terminals/bench/.build/C04-implementation-runner/final-gates/sql-reduce/stdout.log` (SHA256 `EE579D24515100BF5EF71CB7D6BB3CDAD891A399F51CCAD40EE1E22141524D24`); stderr contains normal per-case harness progress (2726 bytes, SHA256 `F5B34EB2BB89EAB33588F7C617F8979D2E3B427F7C8ED4F669409A6C6C7E99C0`); provenance is `provenance.txt` in that directory.

`Blade.exe test interp sql-reduce` exited 0: 48 passed, 0 failed, 0 unsupported skips. The harness includes one provider-reads Zarr M6 check in addition to the 47 sql-reduce fixtures. Full stdout SHA256 `887DD88E9FAAD8E6025C1D3D2673CF9D48E6CC24DC461D36E2EFE11F0C6A27C1`; stderr is empty; provenance SHA256 `9FCFD63ED6166F348F1DC5C5FAED9C5EDF1088B561BFF5EE3C6211AAB8D29E9D`.

`Blade.exe test opt-diff sql-reduce` exited 0: 47 passed, 0 failed, 2 skipped; 31 were optimizer-inert. The two skips are the known reject probes `Reduce Deferred Lambda No Init (rejects)` and `Reduce Deferred Packed Output (rejects)`, both refused by both optimizer modes before differential execution. The 16 changed-emission cases were compiled and run on both sides; all outputs matched. Full stdout SHA256 `6C3296652B22D2BEA241FD55E1DF48EC958156A2831A8F3B380690C8EFF869C5`; stderr is empty; provenance SHA256 `A388FF3ADF2DF7060C8A17485A5A6FDF3DB688F093B33290A9DDE97B6B78161C`.

All three category commands ran from the isolated c-terminals checkout root with `BLADE_PRINT` unset and private `TEMP`/`TMP` under their respective final-gate directories. The same Release executable and DLL identities recorded above were used. No full/default suite, broad categories, sanitizer, oracle, or benchmark was run; full-suite launch count remains 0.
