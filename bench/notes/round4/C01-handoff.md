# C01: PM19 stored-input selected terminal

State: implemented draft, frozen for root review and Luna validation. Base master
`361504b6`; checkout `.round4-worktrees/c-terminals`, branch
`codex/round4-c-terminals`. No worker build, emission, program execution, test,
benchmark, oracle generation or commit has been performed.

## Observed missing route

Master's `planCompactionFusion` retains the named mask and selected array. Its
seeded fold case explicitly declines; `genCompoundInitBinding` still builds the
compact values and index. Positions elision and shared selection are existing
work, not a C01 contribution. The initial target is one direct seeded scalar
fold, not the plan's eventual joined count/sum/sumsq or selected-map forms.

The best reachable idiom is:

```blade
let keep = mask(x, lambda(v) -> v > 0.0)
let y = compound(x, keep)
let result = reduce(y, lambda(a, b) -> a * 2.0 + b, 1.0)
```

`compound` is currently restricted to top-level bindings (BL6002). Therefore the
pilot requires an explicit print selection excluding `keep` and `y`, such as
`--print result`. Default printing observes both arrays and must retain them.
This is the pilot's explicit scope; it does not optimize local compounds or
silently suppress named outputs.

## Contract and implementation

`Optimize.fuseSelectedTerminalModule` rewrites the pair and its single terminal
to `reduce(x, guarded_fold, seed)`. The new callable's body is
`if predicate(cell) then original_fold(acc, cell) else acc`, with original
parameter order and fresh parameter IDs. A rejected row performs no arithmetic
fold step. An empty selection returns the exact seed, including negative zero.
The seed expression remains at its original terminal site and evaluates once.

Admission requires a dominating stored rank-1 plain Float64 input (existing
`storedArrayProof`, including any alias chain), the same source ID at the mask
and selection, a pure/total closed scalar predicate/fold and a closed pure/total
Float64 seed. No calls, captures, casts, keyed reads, unknown expression forms,
thread/device strategies, selected maps, unseeded terminals or multiple terminals
qualify. Every intervening binding's actual `bindingPayload` passes the shared
write analysis; a write through an alias, callee or side table declines.

Only reproducible builds qualify: `Toolchain.fpReproEnabled()` reads
`BLADE_FP_REPRO=1` or the compatibility `BLADE_FP_REASSOC=off` spelling. Default
licensed fold graphs retain their materialized route. The fresh callable has no
commutativity/groups/parallel strategies or sign-parity metadata, and its fresh
`mkFuncArrow` has only value slots and no array identity. Its conditional body is
not a recognized builtin fold. No arithmetic or licence is inherited by accident.

The rewrite runs after monomorphization/array-binop normalization, before
`optimizeModule`, IRLift, CSE, pool-reuse, hot-row, membership and compaction
planning. Whole-program `foreignRefs` is already available at this phase. Thus
no already-built schedule table can retain a deleted mask host or compound ID;
the new callable receives ordinary optimization, lifting and qualification.

The census reuses `compactionRoots`, now including module typedef payloads as
well as binding values/types, function bodies/signatures/captures and side-table
sources/names. Any extra use, alias, tuple/return, capture, keyed lookup, observer,
provider demand or foreign reference retains the original pair. Only the pair's
own known constructor payload/type and the one certified terminal input are
exempt. An unfamiliar print selection also declines.

The pass uses existing `BLADE_COMPACTION_FUSION`; `0`, `b4` and `unshared` retain
the previous tree. `on`/default and `gather` can admit C01. Its separate plan rule
is `selected-terminal v1`, with precise applied/declined evidence.

## Owned files

- `src/Optimize.fs`: typedef roots and narrow tree rewrite.
- `src/Lowering.fs`: early invocation with complete foreign demand.
- `tests/OptimizeTests.fs`: actual C++ storage/route pins, LLVM own-lane
  allocation/serial pins, positive interpreter optimizer-on/off gates with
  independent ordered/empty/zero/cancellation pins, foreign-demand and refusal
  cases (including aliases and a random-fill key hidden writer).
- `tests/corpus/index-types/543_selected_terminal_seeded_order.blade`:
  selected `[1,3,4]`, seed 1, ordered result 22.
- `544_selected_terminal_empty_seed.blade`: result -7.25.
- `545_selected_terminal_cancellation.blade`: per-function repro fallback
  control, strict result 3 even in default builds. Positive cancellation
  admission uses a direct `(+)` kernel inside OptimizeTests under FP_REPRO.
- `546_selected_terminal_intervening_abort.blade`: earlier BL8013 retained.

Existing tracked source files retain CRLF working-tree / LF index policy.
Static `git diff --check` is clean. The runner's initial optimization block
reported 522 passed / 1 failed, as detailed below; the focused test correction
has not been rerun yet. No worker execution has been performed.

## Runner finding and focused correction

The sole failure was `selected_terminal_declines_declared_index_payload`, in
`bench/.build/C01-runner/optimize-root.log`. Its source added an unused
`type Selected = CompoundIdx<keep>` and incorrectly expected a surviving mask
demand. `TypeLower.lowerTypeExpr`'s `TyCompoundIdx _mask` arm (around line 774)
ignores that mask and produces a plain axis whose extent is the placeholder
`IRParam("compound", 0, IRTNat None)`. `Lowering.lowerTypedTypeDef` preserves
the resolved alias. This unused alias contains no `keep` ID for the census to
retain; admission is correct. `TypeLower.lowerIndexType`'s actual index-position
arm (around lines 1257–1294) instead carries `IRCompoundMask(IRVar keep)`.

The corrected tests retain the unused erased alias as an explicitly admitted
control. A separate synthetic IR regression injects an `IRTDIndexType` carrying
the actual `IRCompoundMask(IRVar keep)` payload (the same representation as
`lowerIndexType`) into the lowered program. It verifies local declaration demand
retains both mask and selected value stores with a type-specific decline. It
also places only that declaration in an otherwise empty foreign module,
verifies `moduleReferencedIds` collects the mask ID, and checks the originating
module retains both stores through the foreign-demand gate. No executable
binding/capture/side-table reference can accidentally satisfy this test.

Only `tests/OptimizeTests.fs` and this handoff changed for the correction.
Compiler source, admission/census rules, TypeLower semantics and corpus 543–546
are unchanged from the snapshot already being exercised by the runner. Request
one incremental rebuild and `Blade.exe test optimize` on this new frozen test
snapshot, recording full totals and the two new type-payload results. The ongoing
compiled C++/LLVM comparisons of the unchanged implementation/corpus remain
applicable; no full/default-suite launch is requested.

## Exact targeted runner request

Use this frozen checkout (or a root-approved identical snapshot), a private
working directory for each invocation, and the sole serialized Luna runner.
Do not run the full/default suite.

1. Build `Blade.fsproj -c Release` once. Record the content hashes/revision and
   build result. Fix compilation issues before any interpretation of test totals.
2. Run `Blade.exe test optimize`. This explicitly sets print/repro gates inside
   the new cases, so it exercises C01. It includes independent interpreter
   ON/OFF and actual C++/LLVM emission assertions. Record pass/fail/skip totals
   and every `selected_terminal_*` result. The LLVM assertion must succeed in
   `EmitLlvm.tryEmitProgramNamed`; no silent C++ fallback is acceptable.
3. For corpus 543–546, run the built binary separately with
   `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`,
   `BLADE_PRINT=result`, and each `BLADE_COMPACTION_FUSION` value `on`,
   `unshared` and `0`. The CLI action is `run <absolute-fixture-path>
   --print result`. Compare normalized stdout/stderr and exit status; expected
   scalar results are 22, -7.25 and 3, and fixture 546 must preserve BL8013
   and its original source site. Compare source-site stack details as well as
   the code. Use raw-output/bits mode if available for signed-zero evidence.
4. Emit the same 543 positive in `on` versus `unshared` under those strict
   flags; save emissions and plan decisions. ON must have an applied
   `selected-terminal` record and a serial guarded fold over `x`; it must
   contain no `keep` Bool allocation, `y_compact`, `y_idx`, `y_pos` or gather.
   The `unshared` control must retain mask/value storage and the existing
   positions-elided route. This predecessor comparison avoids crediting C01
   with positions elision already present on master.
5. Request the equivalent LLVM runtime checks for 543–546 where supported,
   verifying the selected backend and recording any explicit unsupported
   refusal/toolchain skip. The in-process LLVM emission pin is the minimum
   capability check; fallback is not evidence of an LLVM execution pass.
6. Target affected existing selection regressions as reviewer/runner resources
   permit, especially index-types 515–542 and sql-reduce seeded cases. Standard
   `test interp index-types` / `test opt-diff index-types` are useful regression
   gates, but their environment scrub clears BLADE_PRINT, so they exercise C01's
   refusal/default-print behavior, not its positive arm. Do not report those
   inert cases as positive optimized-route validation. Positive optimizer ON/OFF
   interpreter gates are in `test optimize`; positive compiled ON/OFF is step 3.

No timing or memory measurements requested before root acceptance. Predicted
storage removal is the private `N`-cell Bool mask, `m` selected values/slack and
compound-index object; the ordinary current positions-elided predecessor already
has no position table. Stored input and required scalar output remain. Allocation
traces/capacity curves and controlled timing are still unmeasured; no memory or
performance win is claimed from this draft alone.
