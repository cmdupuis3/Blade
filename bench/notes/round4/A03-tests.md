# A03 validation

The dispatched A03 validation stages completed successfully on the frozen checkout: one Release build, LLVM facts/optimize, four follow-on categories, eight focused native runs, and four emitted-only controls. Detailed outcomes and limits follow. Subsequent bounded validation also passed 12 affected LLVM controls and 10 sanitizer executions, detailed below. No full/default suite, golden regeneration or benchmark was run. Full-suite launch count remains 0.

## Source and tool identity

- Checkout: `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\a-lifetimes`, clean at commit `0ff2f3d3915fd217760651611d6f7825a69dea63`.
- Handoff `A03-handoff.md` SHA256: `F7CABD4F1EA6875AE3EF107E0FFA40F8D0060CEFB9510A07C8ACCCA4EC740229`.
- Content manifest SHA256: `1FD23043ACB4E4150AAA2FD53DDE63B546D99F3114EBEF4A2838265B15001A72`.
- All four manifest pins matched: `A03-census.md` `1E754EE59AC6A1CE9A77A31F6C24C6FC3E7120DCFFCB42F2D508B565B9AA1BCB`; `src/EmitLlvm.fs` `43293FBA349A5312212D78F45AD5D5E8A7C2DD4EA16E42A26B01A03CB48B3063`; `tests/LlvmTests.fs` `D2B4A019C282F94697D3CAEBD792F04DBCD73E2DBB55321B945D46339C152149`; `tests/corpus/memfree/032_return_reuse_scalar_snapshot.blade` `0A1A3483713EB978362A022DF58EFEC957E7E29105E7D37D46725CBCBC0D7EB0`.
- Release binary `bin/Release/net10.0/Blade.dll` SHA256: `E5C61CCDAE40239242D4EBA54D00B6FCD51D7D1B886B21CF50EE84A6C526B75F`.

## Commands and results

Each command ran serially from the A03 checkout root. `TEMP` and `TMP` both pointed to `bench/.build/A03-runner/tmp`. Relevant inherited Blade environment variables (`BLADE_BLAS`, `BLADE_FP_REPRO`, `BLADE_FP_CONTRACT`, `BLADE_CSE`, `BLADE_OMP_THREADS`, `OMP_NUM_THREADS`, `BLADE_PRINT`, and `BLADE_LLVM`) were unset for both category runs.

1. `dotnet build "C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\a-lifetimes\Blade.fsproj" -c Release` — exit 0; build succeeded with 4 `NU1900` vulnerability-feed warnings and 0 errors.
2. `dotnet "C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\a-lifetimes\bin\Release\net10.0\Blade.dll" test llvm facts` — exit 0; 128 passed, 0 failed, 0 skipped; stderr empty. Both A03 return-reuse positive facts and all eight refusal facts passed.
3. `dotnet "C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\a-lifetimes\bin\Release\net10.0\Blade.dll" test optimize` — exit 0; 608 passed, 0 failed; stderr empty. The full output includes existing C02-L1 tests as part of this optimize block.

## Complete logs

Logs and provenance are in the frozen A03 checkout under `bench/.build/A03-runner`:

- `release-build.log` — SHA256 `C5BC4513922DAE0500A971E802F759009086BC2401D7DD513513DC81359A07A2`.
- `llvm-facts.stdout.log` — SHA256 `6E333CA3F88B3CD20F5FB261CD0928BF9A69A4A1BA4BE1B2E687075D3D3B3473`; `llvm-facts.stderr.log` is empty (SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`); provenance SHA256 `8D561D977D5EA6478FE81AC2C47B4556761C3B3C273A51FE27C41B08C09C71A2`.
- `optimize.stdout.log` — SHA256 `31285BABFF1928AEAB827B8238829ACAAB3DC889B0D88CB0B26A19E17D92CE5B`; `optimize.stderr.log` is empty (same empty-file SHA256); provenance SHA256 `E5339E24C2F55080DDCBD5F43DDD920D9C9E8A8A733E457F14BB7C81499F2877`.

## Follow-on category gates

Commands ran serially from the same checkout root with private `TEMP`/`TMP` under `bench/.build/A03-runner/tmp`, `BLADE_PRINT` unset, and no changed source or binary.

- `test memfree`: **31 passed, 0 failed, 0 skipped**, exit 0. stdout SHA256 `FA0EB22685B6A236F7ADB9F02A7EB8B534BDCE7D4E7BA68DDF27E011379CCE97`; stderr SHA256 `94C910FD4F3CE6DF5EDBE9EF3B58F183A6A306DE3F349D713FAE11EA1E17CE4B`; provenance `CF98CB96B4712CBF91762C7E4A9F9AE6AD511FDE089FCA875529005021410D99`.
- `test interp memfree`: **32 passed, 0 failed, 0 unsupported, 0 skipped**, exit 0. stdout `F36118463B331A97B7CF695FEA6BB819858F96F5E4BE831A1DE5DA916A159B20`; stderr empty; provenance `61D9CF5524E860BA7B1DB68C9E002269EFD358545B62390C45F3FD5263EEA447`. This existing category supplies interpreter pin coverage for fixtures 030/032; no duplicate interpreter runs were added.
- `test llvm memfree`: **21 passed, 0 failed, 10 skipped**, exit 0. All ten are explicit LLVM-lane refusals (4 sparse-index initializer; 2 function rebinding module-level array; 1 each unsupported `IRSort` and `IRStack` array position, module `~` binding, and rank-# compound initializer). stdout `098483B5C81C49FEAE38E703B628AC9E2173380D6EDFE1DBF1AB7C028246DA19`; stderr empty; provenance `FBEC65FF7BB64D8D637CF50BB7EC9D744B46812E93E89968F0FFC0D62EE2776E`.
- `test llvm goldens`: all four groups passed: Compare Rules 23/0/0; Emission Pins 3/0/0; LLVM Fact Layer 128/0/0; Simplex Blocks Agreement 19/0/0. stdout `B7021C144437D1B8C97C53CB7C97D7189246E05252CC1B39E62162A4801B8172`; stderr empty; provenance `4F885E3BAAE5F2803FFCAC9D6A142294DDEB73E7A0FA918B834BEDCE91302307`.

## Focused runtime and emission checks

Eight focused native executions covered fixtures 030 and 032, both pool-reuse settings, and direct C++ and direct LLVM routes. All eight exited 0, all fixture pins matched, and each route assertion confirmed the requested backend rather than fallback. `BLADE_PRINT` was unset (all outputs); strict pins were `FP_REPRO=1`, `FP_CONTRACT=off`, `BLAS=off`, `CSE=0`, `OMP_THREADS=1`, `OMP_NUM_THREADS=1`, with fusion, sibling folds and epilogue legs enabled. Runtime-emitted `.cpp`/`.ll` hashes matched the separately captured probe emissions. Per-arm stdout, stderr, compiler route, source captures, pin results and environment are in the private A03 runner artifacts.

The existing interpreter category above is the interpreter comparison for 030/032; it is separate from the eight native executions.

Four emitted-only probes (no generated program execution) cover 027, 028, 029 and 031. For 027/028, the planner reports reuse candidates, while the LLVM output still contains the original stage allocations; no LLVM reuse is claimed. For 029, C++ emits a physical `copy_n` from p backing v8 into held alias backing v9; q (v13) is freed, but p is conservatively retained despite not being the returned physical backing. For 031, IR contains the scope/release markers and the refusal `scratch reuse requires a region ownership proof`; LLVM releases its scratch backing before the caller map allocations. These are emission observations only.

All per-arm evidence and provenance are listed in `bench/.build/A03-runner/focused/artifact-sha256.tsv` (SHA256 `9ACD79E982C391F92886C08349890FECEB06F22AE2922D3C22504E49ED4CAF72`) in the frozen checkout. No memory-usage or performance conclusion follows from these runs.

## Final C02-L1 LLVM controls and bounded sanitizer checks

The separately dispatched final stage used the same frozen A03 checkout (`0ff2f3d3`) and Release `Blade.dll` SHA256 `E5C61CCDAE40239242D4EBA54D00B6FCD51D7D1B886B21CF50EE84A6C526B75F`; no rebuild or source edit occurred. `BLADE_PRINT` was unset, and the direct LLVM runs pinned `BLADE_LLVM=on`, `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`, `BLADE_OMP_THREADS=1`, `OMP_NUM_THREADS=1`, with fusion toggled per arm. All 12 fixture/settings arms completed: 031 and 035 values passed in both settings; 038 produced `(111.0, 83.0)` in both; 039 produced `[1.0, 7.0]` in both. The expected producer errors also occurred in 036 at its line-8 producer before the later fold error, and in 037 before its row seed. The first wrapper attempt misread Blade's stdout runtime frame as stderr-only; its exit-1 artifact was retained and re-evaluated with a corrected matcher, without rerunning that arm. The wrapper retry and corrected summary are retained separately.

The bounded ASan stage used exactly the four saved 030/032 LLVM emissions plus the optional saved 031 legs=1/reuse=1 emission. Each input received only the authorized `sanitize_address` attribute insertion; reverse replacement matched the original bytes. The adaptation index and complete input/output/tool artifacts are in [the sanitizer artifact manifest](../../../../a-lifetimes/bench/.build/A03-runner/sanitizer-artifact-sha256.tsv) (SHA256 `8BC74672CC6C16D0E7916CBD5B85AB4E1DC8BBC9DFCE2AB70EBCB0B8AA5903FD`). Clang 22.1.8-3 for x86_64-w64-windows-gnu, `llvm-objdump`, `llvm-symbolizer`, and the ASan dynamic runtime were hashed in that manifest's companion toolchain directory; the key tool hashes are clang/clang++ `071874172EDC90EA355DEF3E7E253A27A82646B20B3CA393962621B52F32A708`, objdump `EF30D2A1CB7DA504678AAB7D5F0DADEE4A9001D330D94E5D65230C75F1594AAE`, symbolizer `7079BCFA7F07FEB908A80CC2A6295A69E6B61903E3CEC8AB15E5FEF1368096EC`, and ASan DLL `08DBBAD40F2E5A928AEE9B46874E14409EB615CDB7A41CEB97D659D982F1964A`.

Before executing any diagnostic binary, the checked IR was audited for payload access checks and compiled without a second sanitizer pass; the C shim was compiled with ASan and `BLADE_POOL_CACHE_OFF=1`, then linked. In 030 reuse=1, `bl_returned_pool_3` has `__asan_load8` on q's payload GEP before the load and `__asan_store8` on the returned-p payload GEP before the overwrite; main checks the returned array's post-call reads. In both 032 variants, the returned function has those payload load/store checks, and main checks loads from both returned arrays after both calls. The same `__asan_load8`/`__asan_store8` relocations occur inside the relevant functions in the compiled object; that exact object was linked into each hashed executable. The 031 control is inline in main and carries payload checks; its IR keeps the whole-function reuse refusal and releases the owned scratch before subsequent caller allocations. This establishes exercised payload instrumentation, not general sanitizer coverage.

All four 030/032 LLVM diagnostic executables exited 0, printed every fixture pin, and reported no ASan error. Four C++ CLI `run --memcheck --verbose --no-cache` mirrors (030/032 × reuse 0/1) exited 0, emitted the actual clang64 ASan/cache-off route with `asan=1`, and matched every output. The optional 031 LLVM and C++ memcheck legs=1/reuse=1 executions also exited 0 and matched all six pins. Thus the final bounded sanitizer stage has **10 successful native executions**: four LLVM diagnostics, four C++ memcheck mirrors, and two 031 refusal controls. The C++ process reports nonzero outstanding-byte counts (030: 1,038 bytes/7 blocks; 032: 1,150/9; 031: 1,094/8), which include live program-owned outputs and are not interpreted as leaks or a zero-outstanding requirement. LeakSanitizer is unsupported on this Windows runtime and was not claimed. No RSS, capacity, cache-retention, or performance result follows.

Per-arm compiler, checked IR, object, executable, object disassembly, runtime output and C++ memcheck output are under `bench/.build/A03-runner/asan-diagnostic/<arm>/` in the frozen A checkout. Exact command/env provenance is recorded in [asan-commands-and-provenance.md](../../../../a-lifetimes/bench/.build/A03-runner/asan-commands-and-provenance.md) (SHA256 `8451638544F247A9FEB18AE99E234FBE74D4E46F1A4760FB705C57752F9ADC17`). Toolchain version stdout/stderr and all command output are retained as well. No full/default suite or golden regeneration was run. The direct LLVM matrix and the bounded sanitizer gates are now complete; root review is the remaining step.
