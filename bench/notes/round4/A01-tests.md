# A01 targeted validation

Runner: Luna/high. Historical `a-lifetimes` snapshot at base `361504b60a1edd8a5c06993976daae7d7d25d9d3`; its manifest `b8e5529e8e4b57bb768c157e69244544a53613b1c5f09a1143dbff549492704f` matched all 26 content hashes before that build. The current validation identity is the repaired 29-file manifest recorded below. Earlier snapshots and their compiler/test failures are described in `A01-handoff.md`; their logs remain in the A runner directory.

`dotnet build Blade.fsproj -c Release` ran from the checkout root with TEMP/TMP in `bench/.build/A01-runner/tmp`; it exited 0 with 4 NU1900 warnings because NuGet vulnerability metadata could not be reached. Full output is [build-b8e5529e.log](../../../../a-lifetimes/bench/.build/A01-runner/build-b8e5529e.log). Current `Blade.dll` SHA256 is `348D5725049C01B26A5774B8AD57D6DF551DED6B8742BFCB8ACE8A3EBD11D287`; `Blade.exe` SHA256 is `00522C896F3B5D992E24D4BE54170E114D68E5A059C7D3D5B852D16E1826C622`.

With TEMP/TMP private and the checkout root as CWD, `bin/Release/net10.0/Blade.exe test optimize` exited 0: **511 passed, 0 failed**. Full output is [optimize-b8e5529e.log](../../../../a-lifetimes/bench/.build/A01-runner/optimize-b8e5529e.log). `bin/Release/net10.0/Blade.exe test sibling-folds` exited 0: **37 passed, 0 failed, 0 skipped**, including 37/37 compiled and 32/32 value checks. Full output is [sibling-folds-b8e5529e.log](../../../../a-lifetimes/bench/.build/A01-runner/sibling-folds-b8e5529e.log). `bin/Release/net10.0/Blade.exe test cell-epilogue` exited 0: **87 passed, 0 failed, 0 skipped**; this includes raw-bit gate twins, LLVM parity/failure-order checks, and ASan/census/cache controls. Full output is [cell-epilogue-b8e5529e.log](../../../../a-lifetimes/bench/.build/A01-runner/cell-epilogue-b8e5529e.log); its first line records CWD and the full log path.

The next named gate, `Blade.exe test llvm facts`, exited 1 with **114 passed, 1 failed, 0 skipped**. Its only failure is `inline lifetime cleanup (llvm): Producer-input legs ride the producer's loop across the call that holds it -- gate-off outlined callee lost its pool frees`. Full output is [llvm-facts-b8e5529e.log](../../../../a-lifetimes/bench/.build/A01-runner/llvm-facts-b8e5529e.log). Root has returned A01 for repair; no source changed by the runner. Root requested a stop at this failure boundary.

While the stop request arrived, `Blade.exe test llvm sibling-folds` was already running. The runner interrupted that process at root's request. Its partial log [llvm-sibling-folds-b8e5529e.log](../../../../a-lifetimes/bench/.build/A01-runner/llvm-sibling-folds-b8e5529e.log) contains 22 passes and 1 toolchain skip before interruption, with no final category total; treat this as incomplete, not a passing or failing gate. No further A01 tests have run. The C02 source/emission census is complete; its read-only findings are in [C02-census-results.md](C02-census-results.md). No benchmark or full/default suite was launched; full-suite launch count remains zero.

### 019 lifetime probe and interrupted-gate chronology

After the `llvm facts` failure, I began the next serialized queue item, `llvm sibling-folds`, as part of the previously authorized named-category run. Root's stop request arrived while that category process was active; I interrupted it at that boundary. Its 22 passes and one toolchain skip are partial output only, with no final category total, and are not a completed gate.

A read-only FSI probe against the unchanged Release DLL (`348D5725049C01B26A5774B8AD57D6DF551DED6B8742BFCB8ACE8A3EBD11D287`) emitted LLVM and dumped top-level bindings, callable captures/bodies, and lifetime IR events for sibling-folds/019 with the authored pins. The probe artifacts and complete logs are under [019-lifetime-probe](../../../../a-lifetimes/bench/.build/A01-runner/019-lifetime-probe). Both variants exit 0. On: top-level binding 29 contains `IRBeginPoolScope` and `IRReleasePools` around producer array `v71`; raw LLVM has the 1003-cell allocation at `%r4` and frees `%r60` at the inline return-cleanup site before later caller work. Off: binding 29 retains scalar `IRApp func=v11 ... resultType=Float64`, with no pool-scope/release events; callable `kernel` is capture-free, but raw LLVM contains its work expanded in the caller and has no `@bl_kernel` or `blade_free`. The callee producer's 1003-cell allocation at `%r40` has no corresponding release. `%r83` is a separate resident caller pool and is not part of the scalar-callee finding. This localizes the discrepancy to off-gate tuple emission handling: a scalar call remains in IR, then its body is rendered inline without lifetime scope events. Root's source diagnosis is that `emitTupleParts` handles all `IRApp` values without first checking that the result is a tuple, despite its tuple-specific comment; the genuine tuple path should be guarded so scalar/array calls retain normal `emitCall` handling. This is source evidence only; no runtime or rebuild was run for the probe.

### Repaired tuple-member ABI snapshot (`24b64af9`)

Root-approved manifest `24b64af9b3d2b45866e6c34bab3eb424bb81886450cea9f9d7c066dfbd7e6960` was verified across all 29 content files with no mismatches before rebuilding. `dotnet build Blade.fsproj -c Release` exited 0 with 4 NU1900 warnings and 0 errors. The resulting `Blade.dll` SHA256 is `778364269CB7E56F5433747D455086F7A62295BA676C6DEA0FB315DDB0BDF99B`; `Blade.exe` SHA256 is `00522C896F3B5D992E24D4BE54170E114D68E5A059C7D3D5B852D16E1826C622`. Full output and provenance: [build-24b64af9.log](../../../../a-lifetimes/bench/.build/A01-runner/build-24b64af9.log), [build provenance](../../../../a-lifetimes/bench/.build/A01-runner/build-24b64af9.provenance.txt).

The first requested category, `bin/Release/net10.0/Blade.exe test llvm facts`, exited 1: **117 passed, 1 failed, 0 skipped**. All allocation-specific lifetime checks and scalar/array member ABI checks passed, including scalar-call cleanup and array-call result ownership. The sole failure was `tuple member call ABI (llvm): Genuine tuple returning calls retain the LLVM inline tuple ABI -- wrong ordinary/tuple call ABI or missing callable definition`. No internal `SKIP` lines were observed; the footer reports zero skipped. Full output and provenance: [llvm-facts-24b64af9.log](../../../../a-lifetimes/bench/.build/A01-runner/llvm-facts-24b64af9.log), [provenance](../../../../a-lifetimes/bench/.build/A01-runner/llvm-facts-24b64af9.provenance.txt). The queue stopped at this first failure; no later A01 categories were run on this manifest.

The root-requested read-only 040 tuple probe used the same repaired Release binary and pins `BLADE_FP_REPRO=1`, `BLADE_OMP_THREADS=1`, `BLADE_BLAS=off`, `BLADE_EPILOGUE_LEGS=0`, `BLADE_SIBLING_FOLDS=0`, `BLADE_CSE=0`. FSI lowering/emission exited 0. Binding `_(s,q)` id21 retains `IRApp func=v9(args=[v20])` with tuple result `(Float64, Float64)`; callable `member_pair` id9 is present in IR, has no captures, takes the array input, and returns `(v11,v18)`. LLVM emits no `member_pair`-named definition/call or tuple aggregate operations; the callable body expands into `main`, where scalar results land in `%r50/%r51` and are printed separately. The independent `inputs()` callable remains outlined as `@bl_inputs_2`. Source SHA256 is `A70A35B0AA4D4F9BA2A33AAAAC101495E62A5FC9975C7C92FFED61E38BCE5757`. Probe artifacts: [040-member-pins.ll](../../../../a-lifetimes/bench/.build/A01-runner/040-tuple-probe/040-member-pins.ll), [bindings](../../../../a-lifetimes/bench/.build/A01-runner/040-tuple-probe/040-member-pins.bindings.txt), [callables](../../../../a-lifetimes/bench/.build/A01-runner/040-tuple-probe/040-member-pins.callables.txt), [log](../../../../a-lifetimes/bench/.build/A01-runner/040-tuple-probe/040-corrected.log), [provenance](../../../../a-lifetimes/bench/.build/A01-runner/040-tuple-probe/040-corrected.provenance.txt). This was read-only FSI lowering/emission; no rebuild or category execution followed the failed `llvm facts` run.

### Latest frozen validation (`bafdfcce`)

The latest root-approved manifest `bafdfccef81332208c2aad293bf80a40525b4f8c83d7fd093d041c873cdc1a1b` matched all 29 content files. Release build passed with 4 NU1900 warnings and 0 errors. `Blade.dll` SHA256: `06334B05D5E077FD494A84335300817E09B84BF98F2B1E8C858E4AA0A4BEB355`.

All root-authorized A01 targeted categories passed on that exact binary. Each was run serially from the checkout root with private TEMP/TMP, complete output and an adjacent provenance record under [A01-runner](../../../../a-lifetimes/bench/.build/A01-runner):

| Category | Result | Skip details |
|---|---:|---|
| `llvm facts` | 118 passed, 0 failed | 0 skipped |
| `llvm sibling-folds` | 39 passed, 0 failed | 1 expected LLVM-lane refusal (Float32 unseeded-leg differential) |
| `sibling-folds` | 40 passed, 0 failed | 0 skipped |
| `cell-epilogue` | 87 passed, 0 failed | 0 skipped; seven ASan lifetime controls clean |
| `interp sibling-folds` | 41 passed, 0 failed | 0 skip-unsupported, 0 skipped |
| `llvm tuples` | 17 passed, 0 failed | 26 expected unsupported LLVM/reject-probe skips |
| `llvm functions` | 102 passed, 0 failed | 222 expected unsupported LLVM/reject-probe skips |
| `optimize` | 511 passed, 0 failed | 0 skipped |
| `opt-diff sibling-folds` | 40 passed, 0 failed | 7 optimizer-inert; 0 skipped |
| `omp-coverage` | 15 passed, 0 errors, 0 warnings | 0 skipped |
| `omp-reduce` | 126 passed, 0 failed | 0 skipped |
| `cli` | 202 passed, 0 failed | 0 `[SKIP]` lines |
| `docs` | 132 passed, 0 failed | 78 sketch blocks skipped; 91 run, 10 rejects |
| `corpus-wiring` | 14 passed, 0 failed | 0 skipped |
| `orbit-scatter` | 30 passed, 0 failed | 0 skipped |
| `sql` | 188 passed, 0 failed | 0 skipped |
| `index-types` | 401 passed, 0 failed | 0 skipped |

All per-category full logs were inspected for internal `SKIP`/`FAIL` markers in addition to footer totals. The skips above are the only ones observed. No default/full-suite run or benchmark was launched; full-suite launch count remains zero. Integration of this A01 source snapshot is pending.
