# A04 retry1: bounded raw proof before test repair

Diagnostic proposal only at reviewed source `47311389efb9403a5d8f1c1409450ce7cc318a05`. No production/test change, build, emission or execution by coder. Do not dispatch broad categories. The fresh Release build passed with four NU1900 warnings; LLVM facts were 148 passed / 3 failed / 0 skipped. Optimize was NOT run. Full suite remains zero.

The existing retry1 directory contains only build/facts logs/provenance; it has no new .ll or full IR. Earlier census LLVM is from the preimplementation compiler and different source, so it cannot establish current 033's in-place graph.

## Established versus hypothesized diagnosis

The full facts stdout (`bench/.build/A04-runner/retry1/llvm-facts/stdout.log`, SHA256 `6fb7c5c375f7db8e9e32b1696df54841492ff0de7757abe05e1bec5826b5ccc1`) already records the exact gather decision: `Declined "donor is retained by a callable capture/type payload"`. This is the stronger whole-callable capture check running before the tail-only grammar check the test expected. Its table checks and no-A04-fill condition passed. The test should eventually pin that actual earliest reason, with one refusal and unchanged tables; do not widen production admission to satisfy its stale expected text.

Both positive facts report one admission, unchanged tables, unchanged/fresh C++ and an accepted off-route final-cell graph. Their on-route helper fails `no single old-cell read before same-index store`. The helper currently collects every GEP from donor at the destination loop index, then requires EXACTLY one such GEP and one actual load. The implemented unchanged fillDensePool deliberately emits a source GEP/load and a destination GEP/store; on-route both GEPs have the same backing/index. Thus counting GEPs rather than loaded addresses likely explains the failure. This is a hypothesis until the fresh raw on/off code proves it. No test assertion is changed here.

## Sole-Luna emission-only capture

Use fresh lane DLL `bin/Release/net10.0/Blade.dll`, SHA256 `a7835b8e4849e32215adbecd323ec482c2a0e83f4fcda7567f3d2c26277e65a7`. Verify current source/fixture manifest and DLL identity first. No rebuild is needed or requested. Run from a-lifetimes root with private TEMP/TMP, complete stdout/stderr/exit/environment/tool/script/source/binary provenance and new private output directories. Adapter A04-fact-diagnostic.fsx checks the loaded assembly and frozen 033 fixture SHA and refuses overwrite or unexpected arm values. Coder has only authored/read it, never run it.

Five fixed invocations: positive isolated reuse 1/0; positive actual default scheduler reuse 1/0; gather isolated reuse 1. One source lowering/validation per invocation. Positives emit C++ FIRST and then LLVM from that same IR, exactly like emitSame's paired positive facts; gather goes directly to LLVM, exactly like its negative fact. Program name is `facts`. The adapter sets exactly defaultEmissionEnv + returnPins + samePins, drains separate lower/C++/LLVM decisions and saves full IR, .cpp where applicable, .ll, exact derived source and donor maps. No generated program is compiled or run and no category is invoked.

Example command, for Luna only; substitute the five authorized arm suffix/argument combinations, always using a new output directory:

```powershell
$taskRoot = 'C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/a-lifetimes'
dotnet fsi --reference:"$taskRoot/bin/Release/net10.0/Blade.dll" --exec "$taskRoot/bench/notes/round4/A04-fact-diagnostic.fsx" "$taskRoot/tests/corpus/memfree/033_same_cell_return_owned.blade" "$taskRoot/bench/.build/A04-runner/retry1/raw-positive-isolated-reuse1" 1 isolated positive
```

Other combinations are `0 isolated positive`, `1 default positive`, `0 default positive`, `1 isolated gather`. Outside the exact fact-pinned variables, explicitly unset BLADE_PRINT/TILE_CACHE and record other ambient optimizer gates; preserve the facts process defaults rather than inheriting another workload matrix. Do not disable fusion or alter source to manufacture an admission. Stop unexpected refusal or shape change. Hash all outputs and return the short address/ownership evidence below to root before any repair or test rerun.

## Required raw evidence and proposed repair bound

For each positive on arm, identify the ordinary same_cell function and original 7x8 allocation/tracking slot, completed old_sum and bias stores before the final loop, the final loop's source GEP and actual old-value load, scalar spill/reloads/arithmetic, and destination GEP/store. Prove the two GEPs use the SAME root AND identical SSA index, the old load precedes and feeds the final expression/store, and no other payload load/root/index participates. Show one tracking entry, no free of the escaped root, and return of that exact allocation. Trace caller reads of x/y/a/b after the second call. Preserve flags and full snapshot/fold ordering; source GEP count alone is insufficient.

Off must retain two distinct roots, the same per-cell expression and completed snapshot ordering, original p cleanup through its slot exactly once before returning the fresh result. Confirm C++ on/off remains the earlier two-root fresh path. Gather must retain its actual capture of p, the exact earlier Declined reason already in stdout, no A04 fill/Applied decision and unchanged generic donor maps; save fallback .ll as well.

If raw confirms the predicted harmless duplicate same-index GEPs, a subsequent TEST-ONLY repair may select GEPs that actually feed payload loads, require exactly one old-cell load, and separately require the destination GEP at the same root/index and a later payload store. Do not merely delete the source-count predicate. Retain graph/flags, one-owner/return/free, completed-snapshot, one-admission and two-call/later-access assertions, and reject cross-index or extra old loads. Pin gather's actual first capture refusal rather than accepting arbitrary Declined text. If raw shows any different index, extra load, early store, broken graph or cleanup, return that as a production finding rather than weakening the tests.

No production bug, runtime result, capacity gain or broad validation pass is claimed. Root reviews raw proof and authorizes the test repair/checkpoint before re-running Release -> LLVM facts -> optimize.

The first diagnostic FSI attempt stopped during adapter compilation with FS0041 at write: WriteAllText could not choose its string/ReadOnlySpan overload. It did not lower or emit a diagnostic arm. The author-only repair adds explicit string types to write's name/value and the adjacent fail/sha/decisions helper arguments, plus concrete return types where applicable. No diagnostic behavior, source fixture, compiler or test was changed. Retry remains pending sole-Luna dispatch against the refreshed diagnostic manifest.
