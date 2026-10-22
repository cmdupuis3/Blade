# B08 diagonal initialization: primary validation

The frozen B08 compiler change passed its authorized primary gate. This stage included a fresh Release build and `test optimize` only; emissions and index-types remain unrun.

- Source: `b-measurement` HEAD `a2b82e73b9ec638c5295fb55efa94d82a0bde947`; implementation manifest SHA-256 `85E34A81D3F4475CABACEB7592703D534C1F2D177955937C97A80539F1578B7F`, all 6 changed-file pins and 2 ownership anchors verified. Handoff SHA-256 `58BD35B5308F741C59B7E08609A2CA2ED761F0E3914DC5036BFECFCD36843C2C`.
- Build command: `dotnet build Blade.fsproj -c Release -p:RestoreIgnoreFailedSources=true`, from the B08 checkout root. Existing NuGet package cache was used; .NET SDK `10.0.400`; exit 0, four NU1900 vulnerability-feed warnings and no errors. Build stdout [log](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-implementation/build.stdout.log), SHA-256 `90068D47056297064D0095B6F92B33066983398A0A01051E1E030601AEB6F528`; stderr [log](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-implementation/build.stderr.log), SHA-256 `DB0EBDF4EFD7A10E8211391256A94146CF1390FE2A8D6FF66558BC9ED0682670`; [provenance](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-implementation/build.provenance.txt), SHA-256 `250DA541B31A44A004A368831BB67E7F51A6A049B1A913B19BE9D6A23496A0BA`.
- Fresh compiler hashes: `Blade.dll` `A9D88348F0FBEE1E46F5502B6D3276C410319E97FE4BBAF0FEAEA9F9B3B035AF`; `Blade.exe` `C46D1B5C2DCE8A246CE144C39B469D91BE626262BB25CAF40DF4C71B0B2B8187`.
- `Blade.exe test optimize`: exit 0, **561 passed, 0 failed**, with no internal skips. All **15 PM07** materializer and whole-program route checks passed, including N5/N1, stored-parameter and fallback shapes, and four whole-program emission routes. Full stdout [log](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-implementation/optimize.stdout.log), SHA-256 `6ABC588F6825205567460761CF9CFF0DEB0C24C64F765CDEEFD93EFF4119CA20`; stderr empty, SHA-256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`; [provenance](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-implementation/optimize.provenance.txt), SHA-256 `AE50CAF913862CECEC37EE4909FA723A5F4E94BB9575933CF1F179A22E480041`.

No B08 emissions, index-types category, native execution, or performance measurement was authorized in this primary stage. Root review of the primary result is pending.

## Preserved pre-change baseline report

# B08: two bounded C++ emissions

Both authorized emissions completed with exit 0 using the explicitly selected C05 compiler; no build or generated-program execution was performed. The source-only B08 inputs and manifest matched their requested SHA-256 pins before invocation. `BLADE_PRINT` was unset and the exact CLI selection was `--print anti,result` for both calls.

The compiler was `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals\bin\Release\net10.0\Blade.exe`, SHA-256 `334003542E4DBEA63CB86561DB9D6A4E04606F24EFC3802E71D34895D84754E4`; its DLL was `C7D8B405F8CAFE1561653D76C08F5B5A20EA509D19AA385A2CC91560616E0C11`. Compiler source HEAD was `02672fd0f4ac10c030ab1091014618d826158873`; B08 source checkout HEAD was `d2f50db7f37fa581d71721bf3de834068850be1e` (docs-only after the frozen input snapshot). The request SHA-256 is `967544E1A3168E3828D8D2F1FE0DFD4A6094EB93A15B029CC153BDC4DD8A040A`, the manifest is `69BF1F0F8B6B0EBB00727FFDD776D0FE11613D4B372E659E4C4B011E1183F106`, and the input hashes are recorded in the manifest.

The selected compiler's `CodeGenExpr.fs`, `nested_array_utilities.hpp`, and corpus `035_decompact_antisymmetric.blade` are byte-identical to the B08 source anchors. `CodeGenBinding.fs` has a different whole-file hash: B08 source `829568D273B59E26CE1233C2848A0A387343537E49A1BAF6B803BEDF507AC597`, selected compiler source `6966B828DF2FF6C0298BE39FB93418B3FE1D4590E87E0AE2C7BBB898D0924CBA`. Root reviewed and approved the focused owner-span equivalence: `genDecompactBinding` is unchanged; the C05-only difference is in `genReduceBinding`. This is an owner-span comparison, not a claim that the full files match.

| Input | Exit | Emitted C++ | Observed source structure |
|---|---:|---|---|
| `pm07_anti_n5.blade` (`0FD86F6D1D82FE75B2D92376895F0FEACD676A8007B8FA3886C2E1094FFBC4AB`) | 0 | [emit.cpp](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n5/emit.cpp), SHA-256 `A7284BD81AEA14F94663C308B4287D936FF32FBCE83BCD827B20CBEEC3DF5AB4` | Input A is initialized first; the anti source is filled before dense `result` construction. The emitted result then receives the full zero pass before the 20 signed-pair stores. Both `anti` and complete `result` reach their print loops. |
| `pm07_anti_n1.blade` (`441A1753924BE40B68B1FE161B0115D7850C7A754F4A9D01D0A0A1D0B8A04F9B`) | 0 | [emit.cpp](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n1/emit.cpp), SHA-256 `61DCC731AEF8E0592DC24195385EF706DA4C186BD2C7076D358A882A925C4453` | The one-cell dense result receives its zero store; the strict-pair scatter has an empty loop. Both `anti` and full `result` remain in their print paths. |

Each arm directory contains the emitted source, stdout/stderr, exit code, provenance, deployed runtime headers and a complete per-file hash inventory: [N5 artifacts](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n5), inventory SHA-256 `FE7388E1788DD1B0D69926674430A5ED0D2DD352AEADAB90D408617193375BB5`; and [N1 artifacts](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-emit-census/n1), inventory SHA-256 `1F008EAE984B508C0F2656909B6BC1FA92CF43E949A4B85B150577D7CB11F191`. The exact absolute compiler path, arguments, CWD, source revisions and hashes, and environment are recorded in each `provenance.txt`. Both calls used the B checkout root as CWD, private `bench/.build/B08-emit-census/temp` for TEMP/TMP, FP repro enabled with contraction and BLAS off, OMP emission suppressed, LLVM and tile cache unset, and all other campaign gates unset. No C++ native stores, runtime values, capacity/RSS savings, or performance were measured; the 25+20 and 1+0 write counts remain analytical expectations from the source recipe only.

## B08 compiler emissions after primary validation

After the fresh B08 Release build and optimize gate passed, root authorized a second, separately identified pair using that exact B08 compiler. Both emissions used the unchanged inputs and explicit `--print anti,result`; no build or native execution occurred in this stage. Before execution, `bench/.build/B08-emit-census` was found to contain the earlier root-approved C05-compiler emission pair. Those artifacts were preserved. The B08 pair therefore used a new output root, `bench/.build/B08-primary-emits`.

The B08 compiler was `b-measurement/bin/Release/net10.0/Blade.exe`, SHA-256 `C46D1B5C2DCE8A246CE144C39B469D91BE626262BB25CAF40DF4C71B0B2B8187`; its DLL SHA-256 is `A9D88348F0FBEE1E46F5502B6D3276C410319E97FE4BBAF0FEAEA9F9B3B035AF`. Both calls ran from the B08 checkout root with private `TEMP`/`TMP` under the new output root, `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`, `BLADE_OMP_THREADS=1`, LLVM and tile cache unset, fusion unset/default, and `BLADE_PRINT` selected by the exact CLI arguments.

| Input | Exit | B08 emitted C++ | Observed emitted route |
|---|---:|---|---|
| N5 | 0 | [emit.cpp](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-primary-emits/n5/emit.cpp), SHA-256 `8AED53880B0921F7B92DF129D41B99A476BD5612D312AA572CC0416BF2328F03` | Compact `anti` is allocated and filled before dense `result` construction. `result` is allocated, gets its full diagonal zero pass, then paired signed scatter. Both anti and result remain printed. |
| N1 | 0 | [emit.cpp](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-primary-emits/n1/emit.cpp), SHA-256 `51EFEB466396FDCA5BCB4EBF5F9850CC20CCA55468669A2107DD6655D5F2C677` | Empty strict-pair source remains allocated; dense result receives its one diagonal zero store and the scatter loops are empty. Both outputs remain printed. |

Complete logs, exit codes, per-arm provenance and deployed headers are in each arm directory under `bench/.build/B08-primary-emits`. This confirms the intended emitted source/ownership/print gates for the B08 compiler; store totals remain analytical and there is no runtime or performance claim. The earlier C05-compiler emissions above remain preserved as a separate record.

## B08 `index-types` category

After the fresh B08 build, optimize gate and two compiler-specific emissions,
root authorized the standalone `test index-types` category. Before launch, all
six implementation-manifest files matched (compiler source, OptimizeTests and
new corpus files 554–557); the existing B08 compiler identity was verified:
EXE `C46D1B5C2DCE8A246CE144C39B469D91BE626262BB25CAF40DF4C71B0B2B8187`, DLL
`A9D88348F0FBEE1E46F5502B6D3276C410319E97FE4BBAF0FEAEA9F9B3B035AF`.
No rebuild was performed. The command was `Blade.exe test index-types`, from the
b-measurement checkout root, with private TEMP/TMP and BLADE_PRINT unset.

The category exited 0: **409 passed, 0 failed, 0 skipped**. All four new PM07
cases passed with full output/value pins: N5 complete write, N1 empty scatter,
signed-zero scatter (including reciprocal-sign checks), and integer fallback.
Existing fallback/deferred controls also ran as part of the category. The
summary reports 273 lowered / 136 expected IR rejects, 137 rejection probes,
35 expected aborts, 273 generated C++ cases, 272 compiled, 232 value checks,
and 237 full IR+compile+run passes. One pre-existing reject probe has no
`// ERROR:` detail pin; it still asserts refusal rather than its exact reason.
It is disclosed by the summary but does not count as a skipped or failed test.

Full logs and provenance: [stdout](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-index-types-runner/test-index-types.stdout.log), SHA-256 `21E90D6769E28AF1CFF0BBDB8223C29F7E16A22CDD264425DCECB2599D232915`; [stderr](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-index-types-runner/test-index-types.stderr.log), SHA-256 `FCAB3E5F47D4D963F55C2E6CCA07F941CCD6ABF4E6A7F305A4151F73E4DB28A0`; [provenance](../../../../../../.round4-worktrees/b-measurement/bench/.build/B08-index-types-runner/test-index-types.provenance.txt), SHA-256 `ACBBEBF2201226B609FDF2E04A7DA71EB602513E7BD4D012375A835B70393580`.

The B08 source validation now includes its requested optimize and index-types
categories plus the source-emission checkpoints. No LLVM extension, sanitizer,
benchmark, or full suite ran.