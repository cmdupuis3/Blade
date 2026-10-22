# A03 bounded sanitizer execution brief

Static proposal for root dispatch; Luna alone executes it. This lane only read source/artifacts/tool metadata and calculated expected annotation hashes in memory. No compiler, emitter, generated program or sanitizer was run, and no IR artifact/source was changed. This brief is the only new checkout file. Frozen source is `0ff2f3d3915fd217760651611d6f7825a69dea63`.

## Route and inputs

Build.fs lines 1466–1515, compileLlvmProgram, adds no ASan instrumentation/link flag. Memcheck only adds BLADE_POOL_CACHE_OFF to the shim defines at lines 1237–1253. The emitted functions also lack sanitize_address. Do not call LLVM CLI --memcheck an ASan run. C++ compileMemcheck at lines 504–534 does use clang64 clang++, O0, -fsanitize=address and cacheoff; use that actual C++ route below.

Use only saved `bench/.build/A03-runner/focused/<arm>/arm.ll`: 030-reuse0, 030-reuse1, 032-reuse0 and 032-reuse1, plus optional 031-legs1-reuse1. Original runtime-emitted.ll hashes match the four 030/032 probe emissions. No re-emission or redundant census is needed. Keep originals untouched; write private copies under `bench/.build/A03-runner/asan-diagnostic/<arm>/`.

All definitions in these exact inputs use #0: returned_pool/main in 030/032, inputs/main in 031. Perform exactly one byte-preserving replacement, retaining original encoding/newlines:

```diff
-attributes #0 = { mustprogress norecurse nosync nounwind willreturn }
+attributes #0 = { sanitize_address mustprogress norecurse nosync nounwind willreturn }
```

Assert one old-line occurrence, no prior ASan annotation, and reverse-replacement byte equality. Save exact diff and hashes. No body, declaration, operand, constant, arithmetic flag, allocation, free or other attribute change is authorized. These expected adapted hashes were calculated statically, without writing adapted IR:

|Arm|Original SHA256|Attribute-only copy SHA256|Bytes old/new|
|---|---|---|---|
|030-reuse0|e971a66be1a7de1b4c395807680585909f045a199b75ab5498092e9aed8f7d11|bd0e538c92fd867e24fed6116b8dd5b74e5f3831e2feaf78e026d725d0026787|8541/8558|
|030-reuse1|7d63e25130f50844457df8780a3b1d1cf01277c409de30fc902a437925f4d719|79460456b89bf987b01a66495425b94c6932f18c0a80b5cf3b91451fd4256579|8419/8436|
|032-reuse0|9e67e386822d745e697ba64ce8bf8942515d5107366b2950e701482c704b4e71|2508da4c34ed0162fa549ad5ed5c3e4d2ea80d8584d2ace3115464ed452c88d2|12386/12403|
|032-reuse1|7b3b41020afcc8ec264278732fda915cf98e08b92d59938ef6155e00a68a9d51|270366dbaca5aa04537cb4873448b3c03825b128e55c72706f80610ffc240198|12266/12283|
|031-legs1-reuse1 optional|818ed13ae54834440263104cff5f3241cad77235e192be69c38264723a70eea1|4f4ca40d4eea0504601614b48956676c263b47420b590aa05754c6010d97ae99|12550/12567|

Stop if input identity or attribute inventory differs. No silent stripping of unsupported syntax or replacement with another reference.

## Toolchain and commands

Static inspection found C:/msys64/clang64/bin/clang.exe, clang++.exe, llvm-objdump.exe, llvm-symbolizer.exe and libclang_rt.asan_dynamic-x86_64.dll. Pacman local metadata reports clang 22.1.8-3. No version command was executed here. Original runtime provenance confirms direct LLVM but not the resolved compiler path; diagnostic clang64 is explicitly selected, not inferred to be the original compiler. Luna records actual version/target/compiler/DLL hashes.

Use private TEMP/TMP and prepend clang64/bin to this process PATH for matching runtime DLLs. Set ASAN_OPTIONS=detect_leaks=0:halt_on_error=1:abort_on_error=1:symbolize=1 and ASAN_SYMBOLIZER_PATH=C:/msys64/clang64/bin/llvm-symbolizer.exe. No production cache/object reuse, LTO or benchmark. Example per-arm PowerShell variables:

```powershell
$taskRoot = 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/a-lifetimes'
$taskOut = "$taskRoot/bench/.build/A03-runner/asan-diagnostic/030-reuse1"
$taskClang = 'C:/msys64/clang64/bin/clang.exe'
$taskObjdump = 'C:/msys64/clang64/bin/llvm-objdump.exe'
$taskIR = "$taskOut/program.input.asan.ll"
$taskChecked = "$taskOut/program.instrumented.ll"
$taskProgramObj = "$taskOut/program.asan.o"
$taskShimObj = "$taskOut/shim.asan.cacheoff.o"
$taskExe = "$taskOut/program.asan.exe"
```

After byte adaptation, run each command separately, capture complete stdout/stderr/exit, and stop unexpected failures:

```powershell
& $taskClang -O0 -g -fwrapv -fno-math-errno -fno-omit-frame-pointer -fno-optimize-sibling-calls -fsanitize=address -fno-sanitize-recover=address -mllvm -force-ordered-reductions -mllvm -asan-instrumentation-with-call-threshold=0 -Wno-override-module -S -emit-llvm $taskIR -o $taskChecked
```

Audit the resulting checks BEFORE execution. The threshold option requests explicit access-check calls for easier inspection; if unavailable or no checks appear, stop and return diagnostics. Do not silently remove flags or weaken evidence. The strict attribute-only guarantee applies to the input copy; compiler-produced ASan IR intentionally has new checking instructions.

Then compile that checked IR without requesting another ASan pass, compile the original C shim WITH ASan/cacheoff, and link the ASan runtime:

```powershell
& $taskClang -O0 -g -fwrapv -fno-omit-frame-pointer -Wno-override-module -c $taskChecked -o $taskProgramObj
& $taskClang -std=c17 -O0 -g -fwrapv -fno-omit-frame-pointer -fsanitize=address -fno-sanitize-recover=address -DBLADE_POOL_CACHE_OFF=1 -I "$taskRoot/src/cpp" -c "$taskRoot/src/cpp/blade_llvm_shim.c" -o $taskShimObj
& $taskClang -O0 -g -fsanitize=address -fno-sanitize-recover=address $taskProgramObj $taskShimObj -o $taskExe
& $taskObjdump -dr $taskProgramObj
& $taskObjdump -p $taskExe
```

Hash frozen shim/header (blade_member.h), adapted/checked IR, objects and executable; record target and DLL dependencies. These controls need no tiles object. PATH can resolve matching DLLs; if copying beside the private executable, record identities. Build's C++ lane copies ASan DLL, libc++.dll, libomp.dll and libunwind.dll, all present; direct C/LLVM may need fewer. Missing parser/option/runtime/loader support is an explicit unsupported/toolchain outcome. IR lacks rich source debug metadata; -g cannot invent Blade line locations.

## Required load/store instrumentation proof

Link success, ASan DLL/init, a declaration/import or instrumented shim alone is NOT generated-code access coverage.

- In checked bl_returned_pool_3, map original p/q/result roots to the final-fill payload GEPs. Require actual __asan_load8 and __asan_store8 calls tied through address/ptrtoint dataflow to the double load from q and store into returned p (reuse1) or fresh result (reuse0). If clang uses inline shadow/report checks, audit their dominating control flow instead. Tracking-slot/alloca checks do not substitute for payload checks.
- In 032 main, require checks on array reads after the second returned call, covering both still-required results. Original comments may be dropped by clang; use original root/dataflow mapping, not marker presence.
- Show the same checking-call relocations in the program object within those function ranges, and tie that exact object hash to the linked exe. Save short address/root/access/check excerpts. This proves checks survived lowering, not merely runtime initialization.

Original #0 promises mustprogress/norecurse/nosync/nounwind/willreturn; parameter/allocator declarations include readonly/noalias/align64/nofree. Preserve those input contracts; do not hand-add optnone or weaken assumptions. This exact artifact set has no captures(none) or memory(...) syntax needing compatibility edits. Let the sanitizer pipeline transform attributes, record what remains, and inspect actual checks. O0/no-LTO is a diagnostic configuration, not an O3 equivalence/performance claim. Unsupported attributes or eliminated checks return to root rather than becoming a clean pass. No fault injection or generalized sanitizer support is requested.

## Bounded runs and C++ mirrors

Once input/check proofs pass, execute the four LLVM diagnostic exes for 030/032 reuse 0/1. Keep all outputs and compare EVERY existing corpus EXPECT pin, ignoring only timing when comparing variants. Require exit 0 and no ASan access/UAF/double-free report. Preserve saved strict emission pins: BLADE_FP_REPRO=1, BLADE_FP_CONTRACT=off, BLADE_BLAS=off, BLADE_CSE=0, BLADE_OMP_THREADS=1, OMP_NUM_THREADS=1, BLADE_FUSION=on, BLADE_SIBLING_FOLDS=1 and BLADE_EPILOGUE_LEGS=1. No LLVM Blade-MEMCHECK census is promised: this shim does not install the C++ malloc-hook header.

For actual C++ memcheck mirrors, set BLADE_LLVM=0, BLADE_POOL_REUSE to each 0/1 arm, BLADE_MEMCHECK_CXX=C:/msys64/clang64/bin/clang++.exe, BLADE_EXE_CACHE=0, BLADE_PRINT unset, BLADE_TILE_CACHE/BLADE_POOL_RETAIN unset, and the same strict source pins. Run both fixtures for EACH arm serially:

```powershell
& "$taskRoot/bin/Release/net10.0/Blade.exe" run "$taskRoot/tests/corpus/memfree/030_pm01_existing_array_return_reuse.blade" --memcheck --verbose --no-cache
& "$taskRoot/bin/Release/net10.0/Blade.exe" run "$taskRoot/tests/corpus/memfree/032_return_reuse_scalar_snapshot.blade" --memcheck --verbose --no-cache
```

Capture actual route, clang64 ASan/cacheoff command and fresh memcheck.cpp; the added hook/header means it need not hash-match earlier native.cpp. Verify all pins and available Blade-MEMCHECK asan=1 output. Required main arrays x/final (030), x/y/a/b (032) may remain live to exit: zero outstanding is NOT the criterion. Report hook baseline/CRT skews and actual ownership; LSan unavailable on this Windows runtime is unsupported/skipped, not leak-free.

Optional root-selected 031 boundary control: apply identical annotation/hash adaptation to its saved IR, audit main payload checks and run original all-output pins. Also run 031_pm01_inline_region_refuses_reuse.blade via C++ --memcheck with legs 1/reuse 1. Keep its exact A01 refusal and one scratch release before caller maps. No entire reuse/legs matrix or renewed census is requested.

All payloads are 56 bytes, below 16 KiB eligible retention. Cacheoff makes frees useful for diagnostics; tiny cache-on twins do not test eligible retention. Record commands, hashes, payload-check excerpts, all pins/errors/skips and tool limitations in a short ledger. No production edit, full suite, capacity/RSS/peak, cache-retention or performance claim follows. This is bounded access checking for exercised paths, not proof of every allocator interception or all inputs.
