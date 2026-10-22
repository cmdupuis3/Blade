# C01 targeted validation

**Current disposition: accepted, targeted validation passed.** The corrected frozen snapshot
passed `blade test optimize` with **524 passed, 0 failed, 0 skipped**; its exact Release
build passed. Root integrated source commit `780a5915` as integration commit `8c6571a7`.
The initial typedef interpretation below is historical and superseded: an unused alias
lowers away and leaves no surviving mask root. The corrected tests verify actual local and
foreign IR type-root retention, plus admission when the unused alias is erased.

Runner: Luna/high. Frozen checkout `codex/round4-c-terminals`, base commit `361504b60a1edd8a5c06993976daae7d7d25d9d3`. The requested hashes matched for `src/Optimize.fs` (`02565AD4CB7963CE80253105CAC2067C1026B8B536C20F606C0D0CDF37E9C8AA`), `src/Lowering.fs` (`95C55AA24605160D7F0A7E7E0AC92404B2F44DD4E99DB46891E02C592402ED9A`), `tests/OptimizeTests.fs` (`D375B7FB256B27C910B2646A1FCE3AC09EF635CCE424A1AB2B885A05FDEA2206`) and C01 handoff (`6FA0A8D1C0C2867C616B786A31C3ED5FB58E495920420300587D056B2093C4E7`). Fixture 545 in `tests/corpus/index-types/` has the supplied fixture hash `392FBA7095404FD44C2B81E643890E83F4B156091FE31419531AFBFD1620C272`; the hash manifest's `tests/corpus/optimizations/545_stored_input_selected_terminal.blade` path does not exist in this snapshot.

The exact Release build succeeded with 0 errors and 4 NU1900 warnings because NuGet vulnerability metadata was unreachable. `Blade.dll` SHA-256 is `ba51ab34bdb7e9b0d08c3cc2dfda7a94dfb2412d07f8848fe9dbd5973419368d`; `Blade.exe` SHA-256 is `00522c896f3b5d992e24d4be54170e114d68e5a059c7d3d5b852d16e1826c622`. The host has dotnet 10.0.400 and clang 22.1.8 at `C:\msys64\clang64\bin\clang.exe`.

## Optimize gate

`Blade.exe test optimize` was first started from its scratch directory and reported a missing repository-relative `tests/corpus/ad/008_recarray_grad.blade`; that run was invalid for repository-relative test fixtures. The targeted gate was rerun from the frozen checkout root, as the CLI requires. The correct-cwd result is **522 passed, 1 failed, 0 skipped**.

All positive selected-terminal checks passed, including the actual C++ storage assertion, the in-process LLVM emission assertion (`EmitLlvm.tryEmitProgramNamed`), foreign-demand preservation, ON/OFF interpreter comparisons, and the refusal cases except one in the initial snapshot. Historical initial failure (superseded by the corrected snapshot): `selected_terminal_declines_declared_index_payload` expected an unused `type Selected = CompoundIdx<keep>` alias to retain `keep`. Follow-up type-lowering inspection established that the alias is erased and creates no surviving mask root, so admission is correct. No test was weakened; the corrected tests assert retention for real local and foreign IR type roots instead.

Full output from the valid repository-root invocation is in [optimize-root.log](../../../../c-terminals/bench/.build/C01-runner/optimize-root.log). The earlier wrong-cwd log is retained separately as `optimize-rerun.log` in the same runner directory.

## Compiled values and route evidence

For each fixture 543–546, `Blade.exe run <absolute fixture> --print result` was run with `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`, `BLADE_PRINT=result`, and `BLADE_COMPACTION_FUSION` set to `on`, `unshared` and `0`. The actual runs used the checkout root as cwd; compiler scratch output is private beneath `bench/.build/C01-runner`.

C++ results matched for all 12 runs: fixture 543 returned 22.0, 544 returned -7.25, 545 returned 3.0, and 546 exited 1 with BL8013 integer division by zero at the original fixture's line 5. Normalized output and status are the same across all three gate values for each fixture.

The same 12 combinations ran through the LLVM lane with `BLADE_LLVM=1` and `BLADE_LLVM_CLANG=C:\\msys64\\clang64\\bin\\clang.exe`. The first attempt failed before execution because the sandbox denied the default TEMP directory for the LLVM shim. Re-running with `TEMP` and `TMP` set to the private C01 scratch directory passed all values and source-site checks. A `--verbose` 543 run printed an `[Emit] ... .ll` path, exited 0 and returned 22.0, confirming actual LLVM execution. The strict LLVM plan JSON also reports `lane: llvm` and an applied selected-terminal decision. No `BLADE_RUN_RECORD` file was produced, so lane evidence is the `.ll` emission plus successful program execution.

The ON plan records selected-terminal v1 as applied. Its C++ output contains the guarded fold and no `keep_extents`, compact buffer, index table, position table or compact reads. The `unshared` plan declines selected-terminal, applies the existing compaction-fusion rule with positions elided, and its C++ retains `keep_extents`, `y_compact`, `y_idx` and compact reads (there is no `y_pos`, as expected from the master baseline).

Run records and per-case stdout/stderr are in [fixture-runs](../../../../c-terminals/bench/.build/C01-runner/fixture-runs), [llvm-runs](../../../../c-terminals/bench/.build/C01-runner/llvm-runs), and plan/emission artifacts are in [emit](../../../../c-terminals/bench/.build/C01-runner/emit). No source edits, timing campaign, or full/default `blade test` suite was run. Full-suite launch count remains zero. The typedef census failure blocks C01 until corrected and the failed optimize gate is rerun.

## Focused correction retest

The root-reviewed follow-up changed only `tests/OptimizeTests.fs` and the handoff; compiler sources and corpus fixtures stayed byte-identical to the first tested snapshot. Corrected hashes: `tests/OptimizeTests.fs` `B0D35ABCFDCBF4479911909ACF5A5BF4805B96CF95DFD3F4116DD95B7C878079`; handoff `88F65BFCC2B2ABFACEA6F3F114943EE9B6D77479B61407D9F324AB5F2763C9AB`. The incremental Release rebuild exited 0 with 2 NU1900 warnings. Current `Blade.dll` hash is `5666d72aeb1f6a445d4faa9823b4b4eaa0b694a367f4eb34927d224cba8472ea`.

Rerunning only `blade test optimize` from the worktree root exited 0: **524 passed, 0 failed, 0 skipped**. The corrected `selected_terminal_declared_index_payload_local_and_foreign` gate proves that an actual declared-index mask payload retains storage locally and through a foreign-module demand, while `selected_terminal_erased_unused_alias_admitted` confirms the unused alias is correctly discarded. The pre-fix C++ and LLVM compiled fixture comparisons remain implementation-identical: only test assertions changed, and compiler/corpus hashes match. The new corrected optimize output is [optimize-corrected.log](../../../../c-terminals/bench/.build/C01-runner/optimize-corrected.log).
