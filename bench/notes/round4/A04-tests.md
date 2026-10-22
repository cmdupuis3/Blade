# A04 bounded validation

**Current status:** A04 primary, follow-on categories, four 033 default-scheduler runtime arms, and the four-arm bounded sanitizer dispatch passed. LLVM facts are 151/0/0 and optimize 653/0/0; LLVM memfree has 10 explicit lane-refusal skips. The sanitizer evidence is recorded for root review; it does not establish leak freedom, cache retention, capacity, or performance.
## Reviewed emission-only retry `a7e6b555`

The 11-entry A04 implementation manifest and two-entry diagnostic manifest matched, as did the frozen 033 fixture and the existing Release DLL. HEAD was `a7e6b55557da79a3592b5acd586211468f0ca5a2`; implementation manifest SHA256 `FCB79392D9661D4CD1DAAB738D5C96708FA7B956D20291882FECCFDFC36E2986`; diagnostic manifest SHA256 `56E6A411AAFCFD16F23D1D7BF6BA5366A2B5DD4D8C32F6A675F6D4807662462B`; request SHA256 `1E7886857B83A425A791C4982C079C7B4DB558F98EFE8E9F2A75D54FF5CBF522`; adapter SHA256 `1F96F98B6D0F768B14AB45A3E61AD26201A98D229523CBECBF6C4934F0310B7F`; DLL SHA256 `A7835B8E4849E32215ADBECD323EC482C2A0E83F4FCDA7567F3D2C26277E65A7`; fixture SHA256 `613343A9C3F9F9766A3E4BBA85BC3FC9CE39349EC7428C9A876D36796646DBBD`.

All five fixed FSI invocations exited 0: positive isolated reuse1/reuse0, positive default-scheduler reuse1/reuse0, and isolated reuse1 gather. The adapter lowered/validated each source once. It emitted C++ before LLVM for the four positive arms and LLVM only for the gather control. No generated program was compiled or executed; no test category was run. .NET SDK was 10.0.400. `TEMP`/`TMP` were private under `a-lifetimes/bench/.build/A04-runner/retry2/tmp`; `BLADE_PRINT` and `BLADE_TILE_CACHE` were unset. The ambient BLADE environment snapshot and all per-arm commands, exits, hashes and provenance are in `a-lifetimes/bench/.build/A04-runner/retry2/`.

Both reuse1 LLVM positive arms record `same-cell-return-llvm` applied. In each, `%r128` is the single 7×8 cell backing and `%r129` is its tracking slot. The producer fill, first fold and bias calculation finish before the final loop. That loop forms source GEP `%r176` and destination GEP `%r183` from the same `%r128` root and the same SSA index `%r174`. It loads the prior cell through `%r176` (`%r177`), carries that value through the multiply and addition (`%r180`, `%r182`), then stores to `%r183`. The function returns `%r128` and performs no free of it. The raw function is at lines 24–111 of the positive LLVM files. Isolated reuse1 LLVM SHA256 `8D33F384A47042C7060841AC106509C77B52CD89523E32E305BB0B0F21D21408`; default reuse1 LLVM SHA256 `8D33F384A47042C7060841AC106509C77B52CD89523E32E305BB0B0F21D21408`.

Reuse0 LLVM keeps the old `%r128` backing and allocates a fresh `%r170` result. It reads `%r128` through `%r178` and stores the transformed value to `%r170` through `%r185`; after the loop, it loads the old pointer from `%r129`, frees it once, and returns `%r170`. Isolated/default reuse0 LLVM SHA256 `8D1855FC5A3579D978F1ABDE75AE37FF783B7C94CCCF3F3625FF4AB63DFD6267`. C++ remains the two-root fresh path in all positive arms: `same_cell` allocates `__v12` and `__ret1073741824`, fills the latter from the former, deallocates `__v12`, and returns the fresh result. The C++ SHA256 is `2ACB4BEFE7411CDD3E653C32947C4E7406037F6BEBE0F625920D467FE1264384` in all four positive arms.

The gather control's LLVM decision retains the exact earliest refusal, `donor is retained by a callable capture/type payload`; it has no A04 fill or applied decision. Donor tables were empty before and unchanged after every invocation (`unchanged=true`). Gather LLVM SHA256 `5E6612A9B78C2901BD87170886D71323ADBC977B4FE19B3E6BFD46911198CC8B`.

Full IR, C++/LLVM text, exact source, decisions, donor maps, and per-artifact hash lists are retained in `a-lifetimes/bench/.build/A04-runner/retry2/raw/` and `.../logs/`. FSI stdout/stderr are empty for each successful invocation (empty-file SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`); campaign provenance SHA256 `18CE62099936446CE197EC1DE263680944CC7BA327C05C06609DA8B1FB8F969F`; ambient-environment snapshot SHA256 `EF2C22690A2151995685B322DFA1D3F8B49AE0813B5039B700A21E6884A54517`. This is emitted-code evidence only. The failing LLVM facts assertions remain unresolved pending root review; no source/test change or build occurred.


## A04 test-only retry `52f4bcc3`

The frozen checkout was `a-lifetimes` HEAD `52f4bcc31849def15ee50e0ddce353bf7113cbab`. The A04 implementation manifest SHA256 `AEA2F29585459CE5D9E01593A0A79782E806B48DAC5F6DC42C3A396F03524B09` and every listed source/input hash matched. `tests/LlvmTests.fs` was `F05C626C01D8697DBAA2F7F963B555D6A1CAC8A84CB5360F4B0EEADB2846CAFF`; production files `src/EmitLlvm.fs` and `src/Types.fs` matched the frozen manifest. The root-reviewed diff was the test-only matcher/refusal repair; no source or fixture was changed during validation.

A fresh `dotnet build Blade.fsproj -c Release` passed with 4 NU1900 warnings and 0 errors using .NET SDK 10.0.400. The fresh `Blade.dll` SHA256 is `CF599009323132F2805139134CF4333C835C3170B6CA859CC7537C5DBE705B03`; `Blade.exe` SHA256 is `98DBD84687FB4EA95756C1D7C4AF5BD2036A7CBC8BBF3F6CE74F6B3A3DD47CCD`.

`Blade.exe test llvm facts` passed **151, 0 failed, 0 skipped**. `Blade.exe test optimize` then passed **653, 0 failed**. The complete logs and provenance are in `a-lifetimes/bench/.build/A04-primaryretry52f4/{build,llvm-facts,optimize}/`; build stdout SHA256 `636263308243C591E8078A0FFFC8BC60E97E8F515AA3E4114F3A9C1FD42E4669`, facts stdout `72D5AC255196E993F23E9F6C9008130FF5B6326C6A828CEE1F02853B8AC8EEAB`, optimize stdout `294E58980E20A9EDE5858DA2FEF0E7F81B4C9FA5D9C4A86CCFE481B9CDDEF4C1`; all stderr logs are empty. The optimize output contains no internal skip markers. `TEMP`/`TMP` were private at `a-lifetimes/bench/.build/A04-primaryretry52f4/tmp`, checkout root was CWD, and PRINT was unset. No other category, corpus execution, sanitizer, benchmark, or full suite was run.

This completes only the newly authorized primary gates. The separately dispatched category and runtime follow-on is recorded below; sanitizer work remains pending review.

## A04 category and 033 runtime follow-on `52f4bcc3`

The same frozen A04 checkout and fresh Release binary were used: HEAD
`52f4bcc31849def15ee50e0ddce353bf7113cbab`, `Blade.exe` SHA256
`98DBD84687FB4EA95756C1D7C4AF5BD2036A7CBC8BBF3F6CE74F6B3A3DD47CCD`, and
`Blade.dll` SHA256
`CF599009323132F2805139134CF4333C835C3170B6CA859CC7537C5DBE705B03`. The 033
fixture SHA256 is `613343A9C3F9F9766A3E4BBA85BC3FC9CE39349EC7428C9A876D36796646DBBD`.
No source or test fixture changed after the primary build.

The serialized category commands were `Blade.exe test memfree`,
`Blade.exe test interp memfree`, `Blade.exe test llvm memfree`, and
`Blade.exe test llvm goldens`, each from the a-lifetimes checkout root with its
own private `TEMP`/`TMP`, all output required, and the Blade policy environment
unset as recorded per invocation. Results were respectively 32/0/0, 33/0/0,
22/0/10, and 196/0/0. The 10 direct-LLVM memfree skips are explicit lane refusals:
unsupported sort/stack, whole-module mutable-array rebinding, compound/module
position or rank-two compound initialization, and sparse-index initialization.
The LLVM goldens total is 23 compare rules + 3 emission pins + 151 facts + 19
simplex blocks, with no skipped group. Full stdout/stderr, exit files and invocation
environment are in `a-lifetimes/bench/.build/A04-followon52f4/`; stdout SHA256s
in the same order are `11E66DD1DB06879FDF7389A8A2BF400C1E93102760964690095800350AB9B83A`,
`96D5E9BB90535F2A77220F6A995FCB240EA7DD86E495D94FFCB69B4F2DAE545E`,
`EF8BA63570D84A761784C2198B4B01293B061B548CA5C69FE6D12B89AA71BF89`, and
`C282D853D60F19BB45D184F953B7942C34A00A945B53C617DD72BBD47E7C89CF`.

Four serial `Blade.exe run <033 fixture> --verbose --no-cache` calls used
`BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`,
`BLADE_OMP_THREADS=1`, `OMP_NUM_THREADS=1`, `BLADE_CSE=0`, `BLADE_EXE_CACHE=0`,
`BLADE_FUSION=on`, and `BLADE_POOL_REUSE` toggled 1/0. `BLADE_PRINT` and
`BLADE_TILE_CACHE` were unset, so all six fixture outputs were checked. The actual
default-scheduler knobs `BLADE_HOT_ROW`, `BLADE_CELL_EPILOGUE`, and
`BLADE_EPILOGUE_LEGS` were unset. For each run, a separate `Blade.exe plan <033
fixture> --json` call captured the shared plan. Both C++ runs emitted `.cpp`; both
LLVM runs emitted `.ll` and did not fall back. All four exits were 0 and every one
matched the six independent fixture pins.

The generated C++ function is identical for reuse on/off (SHA256
`04494AFE08CD8B09B721A64B988E627537083E970F235F941719070B17C8DC51`): two local
array allocations, a fresh result fill, one donor deallocation before return.
Direct LLVM reuse=0 has two `blade_alloc_cells(7, 8)` sites and one donor
`blade_free` (emitted `.ll` SHA256
`95FEEE75BC0BEDFD2765F6CAFBBCCA08B65EFED29E96D55D928BD908FCE62B49`). LLVM
reuse=1 has one such site and no callee free (`.ll` SHA256
`0A60A99D3CD348ECE3DCAB1B01AE7363ED73ADFA4247820403FDBF5B8B08D713`). The all-output
stdout, route stderr, `--json` plan, invocation, and source artifact are retained
per arm under `a-lifetimes/bench/.build/A04-033-default-52f4/`. The generic shared
plan reports `pool-reuse` declined in all arms; it does not print a separate
backend-specific same-cell diagnostic. The LLVM emitted shape is therefore the
recorded runtime-path evidence for root's raw review, not a claimed textual marker.
Exact run logs, plans, source emissions, executables, invocations and the setup-error
note are covered by `A04-033-sha256.tsv` (SHA256
`A7D64EBB9DEA9E71E3B24E410EA90B8990BE6395BEA3D3B854754F97DA2EB9A5`). The per-arm
executable hashes are C++ reuse1 `E7C4977E27205B6126CF3C534E565E2EF8102E285A3B6805133112AF41DDB8E0`,
C++ reuse0 `040FD6953C1059E67B12731400B2C0281D83957292D990292139836B0250C81A`,
LLVM reuse1 `F180569869BB874A4C54AD18CB400BE71E96C8EBA691B9AE143D930CFA647EBA`, and
LLVM reuse0 `408F121DE95A1DA920EEB70FF533F05F197BE4DF9D0BE599527D02E8B3B4705D`.

A local PowerShell command-construction snippet for the C++ reuse=0 arm initially
failed to parse before any Blade process launched; the corrected invocation ran
once and passed. That wrapper-only error is preserved at
`a-lifetimes/bench/.build/A04-033-default-52f4/cpp-reuse0.setup-error.txt`
(SHA256 `06454F39999354842D202D03D1D6D184A0644482ACCC4E9AD66B48DE91C3F081`) and is
not counted as a Blade attempt. No sanitizer, capacity measurement,
performance claim, or full-suite run was made; full-suite launch count remains 0.
## Earlier primary LLVM facts checkpoint

**Historical result:** the prior primary build succeeded, then LLVM facts reported 148 passed, 3 failed, and 0 skipped. The three failures were the two positive same-cell old-load-before-store checks and the gather refusal assertion. That gate was not rerun during this diagnostic checkpoint.
## Frozen identity

The runner used `a-lifetimes` at clean HEAD `416f950424449f9ea790bc74537807d64070e9c5`. The 11-entry `A04-implementation-sha256.json` matched; its SHA256 is `6D1EC9B7840836071573455E8C9F4AF4CFD0BB0AFEF577E8C8E901C591B0961F`. The handoff SHA256 is `9009D8698FBDD645B1D009B1C80092FB7EB15BBB84A158CA380D0114F9389F74`.

## Build result

Command: `dotnet build Blade.fsproj -c Release`, from the a-lifetimes checkout root, with private `TEMP`/`TMP` under `bench/.build/A04-runner/build/tmp`. The build exited 1 after 23.65 seconds. It reported four NU1900 package metadata warnings and six F# errors:

- `src/EmitLlvm.fs(7922,152)`: expected `BufferDimGroup`, received `IRIndexTypeG<'a>`.
- `src/EmitLlvm.fs(7928,61)`: `BufferDimGroup` does not match `IRIndexTypeG<IRExpr>`.
- `src/EmitLlvm.fs(7929,55)` and `(7929,62)`: the same mismatch, including a mixed-type list.
- `tests/LlvmTests.fs(862,36)`: the `if` expression is required to produce `Result<string,string>` but returns `unit` (reported twice).

Full stdout is `a-lifetimes/bench/.build/A04-runner/build/stdout.log`, SHA256 `FC956D00BD115F0D74D31FFF7E12C54303EEDB7E88AE9D0B4CF8AFBBF71AA872`; stderr is empty, SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`. Provenance is `build/provenance.txt`, SHA256 `3993CF524F0B730D5BEA8A6383CEE2602C3BED09393F362FCD273CBF5189D246`. The build environment used .NET SDK 10.0.400; source manifest and handoff hashes are recorded in provenance.

No LLVM facts or optimize command was run. Any binary already present in the checkout is stale for this failed build and was not used. No source was edited and no category, full suite, sanitizer, oracle, or benchmark was run; full-suite launch count remains 0.

## Reviewed repair retry

The type-annotation and missing test-error-return repair was reviewed and frozen
at `a-lifetimes` HEAD `47311389efb9403a5d8f1c1409450ce7cc318a05`. All 11
entries matched manifest `FCB79392D9661D4CD1DAAB738D5C96708FA7B956D20291882FECCFDFC36E2986`;
the handoff SHA256 is
`1876931729B3A85E71C5F8FA5F5D0D045ED8F31DF4B7DDA5DFC4FB8DF8FF7F6B`.

The fresh Release build passed with 4 NU1900 warnings and 0 errors. Build
stdout is `a-lifetimes/bench/.build/A04-runner/retry1/build/stdout.log` (SHA256
`CF9A6F068169922724BEAC9BCD89487B976AF9584331041AF9509988177C8D22`); stderr
is empty. Fresh `Blade.dll` SHA256 is
`A7835B8E4849E32215ADBECD323EC482C2A0E83F4FCDA7567F3D2C26277E65A7`, and
`Blade.exe` SHA256 is `EA70F1C12441551A2DCD2B5DA8C3299F476EE046A566A73D7D1506F53C0F9C97`.

`Blade.exe test llvm facts` exited 1: **148 passed, 3 failed, 0 skipped**.
The two `same-cell return` positive facts (`isolated ordinary maps` and
`actual default scheduler`) each report `no single old-cell read before
same-index store` while showing one admission. The `gather closure` refusal fact
reports `declined=false`, with planner tables unchanged and failure retained.
Full stdout is `a-lifetimes/bench/.build/A04-runner/retry1/llvm-facts/stdout.log`
(SHA256 `6FB7C5C375F7DB8E9E32B1696DF54841492FF0DE7757ABE05E1BEC5826B5CCC1`);
stderr is empty; provenance SHA256
`040A8F4DC05691982CA327BD5AAA35E0C0B67A8E4F6C0D287B30096F07660A71`.
Execution stopped at this first failed gate; `test optimize` was not run. No
source edit, broader category, full suite, sanitizer, oracle, or benchmark was
performed. Full-suite launch count remains 0.

## Emission-only diagnostic adapter attempt

The diagnostic manifest `A04-fact-diagnostic-sha256.json` matched (SHA256
`0535902D0A0EE68B9BD4C33FAD2F69EEF067681D1836854F732CBC991A16F545`), including
the adapter SHA256 `88E880D35AD7285BF1059B12C96FB187EB57A627729FE6DDADDC2A82000F4C4E`.
The existing fresh DLL matched the required SHA256
`A7835B8E4849E32215ADBECD323EC482C2A0E83F4FCDA7567F3D2C26277E65A7`. The first
authorized arm, isolated positive reuse1, exited 1 before creating diagnostic
artifacts: FSI reported `A04-fact-diagnostic.fsx(39,29): FS0041`, an ambiguous
`File.WriteAllText` overload because the contents argument is inferred as an
unknown type (`ReadOnlySpan<char>` or `string`). No other arms were invoked.

The captured stderr is
`a-lifetimes/bench/.build/A04-runner/retry1/raw/isolated-positive-reuse1/stderr.log`
(SHA256 `5AAC36EFBC89DD2E288BD9C4A4D3F718F085214E693D6218E5850F6709E6C32C`);
stdout is empty (SHA256
`E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`). Provenance
SHA256 is `E2F2C4939BD03CB2E7FBF43536297DF94E3A612EB88AD506A9D5C42164401C6E`.
This is a diagnostic-adapter failure, not an A04 source or runtime result. No
adapter retry or additional test was run.

## Bounded 033 sanitizer diagnostics on `52f4bcc3`

The authoritative dispatch is the integration `A04-sanitizer-dispatch.md` (commit `254dc568`). The frozen A lane remained at `52f4bcc31849def15ee50e0ddce353bf7113cbab`; the existing `Blade.dll` SHA256 was `CF599009323132F2805139134CF4333C835C3170B6CA859CC7537C5DBE705B03`, `Blade.exe` `98DBD84687FB4EA95756C1D7C4AF5BD2036A7CBC8BBF3F6CE74F6B3A3DD47CCD`, and the 033 corpus fixture `613343A9C3F9F9766A3E4BBA85BC3FC9CE39349EC7428C9A876D36796646DBBD`. The source shim was `172754BE5A4E4A1976C8E0762304C90F87D79664CBE09695D71645FE05BA7888`; `blade_member.h` was `122724CF46DB3EFA6B03979888A8C4F0A77E742378B49C50D08677B7325285FC`. No tracked source changed.

Exactly four runtime arms ran. For the two LLVM arms, I copied the saved default-scheduler 033 IR and inserted only `sanitize_address ` after `attributes #0 = { `. The ON input retained `nofree`; the OFF input retained its original omission. Reverse replacement matched each original byte-for-byte, and input/adapted hashes and lengths are recorded in `a-lifetimes/bench/.build/A04-sanitizer-52f4/{llvm-reuse1,llvm-reuse0}/adaptation.provenance.txt`. Each adapted file passed clang64 ASan instrumentation with exit 0, then compiled and linked with the ASan cache-off shim. The resulting reuse1/reuse0 program object hashes are `71554FEF3B0D7DC54301262ECF7E1538EDBBAEFAA2156979826B8CED771FB736` and `31A4D5F14FFDD433434F36259DEC8A8CE574AB8208650944C632620B93508D13`; the single shim object hash is `1FC937C935427AA7153CF9CC3B50F010F51AC56B45499D2BBB9F0575F6D46060` (built with `-DBLADE_POOL_CACHE_OFF=1`).

The checked `bl_same_cell_3` ON path proves the old payload load through `%r176` at index `%r174` reaches `%r177`, then the multiply/add, before a checked store through `%r183` at the same `%r128` root and index. OFF checks the old `%r128` load through `%r178` and the new `%r170` result store through `%r185`, then frees the old producer once. Both object relocation listings contain the corresponding `__asan_load8`/`__asan_store8` references inside `bl_same_cell_3`; `main` has checking-call relocations around output reads after both function calls for original `x/y` and returned `a/b`. The exact IR excerpts, object relocations, and address/root summary are in `a-lifetimes/bench/.build/A04-sanitizer-52f4/payload-check-review.txt` (SHA256 `A706D72C25C9E86A35AF7F649962D9367757B66AA38DBE5195A9748040A45C83`). The linked executables depend on `libclang_rt.asan_dynamic-x86_64.dll`; matching compiler/runtime identities and all linked-object hashes are retained in the artifact tree.

Both LLVM diagnostics exited 0, matched all six 033 pins, and had empty stderr: reuse1 stdout SHA256 `CE3F3A0D761FA6B376127E94A5BCB95478CAF8A96B4749DE7628BEB08B31DC1E`, reuse0 `4205699666BFE7D56E777475F804CFA99B94CDAD6DBE5884BD00442356A1118B`. The linked executables are `8160549645850353C252C1DF2C9FC596395CF40C575F1F6D7AC4CED8304C6F93` and `8C60B7272E058178AEE96E92976D7DE1411564A6D986AAFD3C9DF522D4BFD4BD`.

The two C++ mirrors ran the original fixture with `--memcheck --verbose --no-cache`, reuse 1 and 0, `BLADE_LLVM=0`, explicit `BLADE_MEMCHECK_CXX=C:\msys64\clang64\bin\clang++.exe`, strict reproducible/noncontracted arithmetic, BLAS/cache/CSE off, serial OMP, and all output pins. Both exited 0 and matched all six EXPECT lines with no ASan error. `BLADE-MEMCHECK` reported `asan=1`, `baseline_bytes=49804`, `final_bytes=50954`, `outstanding_bytes=1150`, `outstanding_blocks=9`, `allocs=198`, `frees=145` for each run. The returned arrays are still live at exit; this outstanding count is not classified as a leak. The CLI did not print child compiler argv, so the exact clang++ argument vector was reconstructed from the captured resolved compiler/source paths and frozen `src/Build.fs:522-523`: `-std=c++17 -O0 -g -fwrapv -fopenmp -fsanitize=address -fbracket-depth=1024 -Wno-c++20-extensions -DBLADE_POOL_CACHE_OFF=1 -o <exe> <cpp>`. Generated C++ SHA256 is `9366F87D1AE6E73D7F9DFA25D43225CD3BC960D86F6B37CA935A19A18D2E322C` in both arms; generated executables are `05889EB00D4932BCAAB4830D63F98D608F65E622BE8C6C18F351300D8B47D20E` and `800C18762A327DC2B46C39CA20C1B42AFFEA928D305D953AF25AD88537FC99C9`. Full stdout/stderr and provenance are under `a-lifetimes/bench/.build/A04-sanitizer-52f4/cpp-reuse{1,0}/`; generated programs and their runtime DLL dependencies are preserved in each `native-cpp/` directory.

clang64 clang 22.1.8 targeted `x86_64-w64-windows-gnu`. Tool hashes: clang/clang++ `071874172EDC90EA355DEF3E7E253A27A82646B20B3CA393962621B52F32A708`, llvm-objdump `EF30D2A1CB7DA504678AAB7D5F0DADEE4A9001D330D94E5D65230C75F1594AAE`, llvm-symbolizer `7079BCFA7F07FEB908A80CC2A6295A69E6B61903E3CEC8AB15E5FEF1368096EC`, and ASan runtime DLL `08DBBAD40F2E5A928AEE9B46874E14409EB615CDB7A41CEB97D659D982F1964A`. `ASAN_OPTIONS=detect_leaks=0:halt_on_error=1:abort_on_error=1:symbolize=1`; therefore LSan was deliberately disabled/unsupported and no leak-freedom claim follows. These 56-byte arrays do not measure cache retention. No performance/capacity measurement, broad category, or full-suite run occurred. Complete 69-file artifact inventory: `a-lifetimes/bench/.build/A04-sanitizer-52f4/A04-sanitizer-sha256.tsv` (SHA256 `6A357D017809F4AD09EE66720232D1631773CEB5FCB9100763EEC64FA214A1D0`).