# C03 handoff: private selection feeding two seeded tuple folds

Frozen review checkpoint on `codex/round4-c-terminals`, base
`7b2ff8d067845f52ea64ee1ff5fa8ae779826bd5`. Source inspection/editing and static
diff checks only by coding lane C. No build, lowering, emission, interpreter,
execution, oracle generation or benchmark was performed by the coder.
Root remains final reviewer; Luna is the sole runner. No full suite requested.

### Initial build repair

Root froze the original checkpoint as `74d02b820eb21cc8c33c51145fc1ba6125516ee4`.
Luna's first Release build stopped with one FS0039 at OptimizeTests.fs:5687:
the synthetic witness annotation named `IR.IRId`, but the alias is declared in
`Blade.Types` (Types.fs:7). No optimize tests ran. Logs remain in
`bench/.build/C03-implementation-runner/release-build.stdout.log` and its stderr/
provenance companions. The only source repair changes that annotation to
`Set<Blade.Types.IRId>`. Production and all seven corpus hashes are unchanged.
This repair has not been built or executed by the coder; retry the staged
build -> optimize gate before native arms.

## Change and proof boundary

Only production edit: the existing `fuseSelectedTerminalModule` in
`src/Optimize.fs`. C01's single-terminal arm remains. One additional arm accepts
an exact two-element tuple of direct seeded folds over the same private compound.
The mask, compound and tuple must be adjacent executable bindings. Matching
uses binding IDs and IR shape, without generated-name proofs or cross-binding
joins. Rewriting precedes ordinary optimization, IRLift/CSE and planner tables;
the existing sibling pass and both backends are unchanged.

Keep C01's stored dense Float64 source proof, same mask/compound source version,
capture-free closed total scalar predicate/folds and closed total scalar seeds.
The tuple additionally requires a literal nonnegative plain axis and certifies
the mask input/type, expected compound placeholder/type, both selected operand
types and plain scalar callable arrows. Unknown or structured metadata declines.
New guards have fresh parameters/callable IDs, plain function arrows, no parallel
or reassociation licence, and derived-function origins. Each false predicate
returns the original accumulator; no fold step occurs for a rejected cell.
Each seed stays attached to its own fold and is evaluated once. The existing
join may move the second CLOSED TOTAL seed before the first chain, which cannot
change a failure/effect or source read. It retains two independent accumulators.

The residual census subtracts only the two certified inputs under the actual
host ID. Both kernels/seeds and all other roots remain audited: host type/side
payloads, other bindings (including reference-equal trees), types/embedded extents,
captures, program-wide callable bodies, provider references, foreign IDs and
print demands. Mask/compound metadata is certified rather than generally ignored.
Unexpected execution side payloads decline even without private-ID references.
Full payload write analysis remains active; lifetime events are rejected in the
tuple interval and stored-source initializer/alias chain. Adjacency refuses
intervening aborts or unrelated calls without treating CSE repeatability as totality.

Explicit valid `BLADE_PRINT` selection must exclude mask/selection. Default
printing observes both. Compound remains top-level-only (BL6002); this card does
not expand that language scope. No unseeded, selected-map, nonliteral-axis,
cross-binding, dependent-seed, third-consumer, unknown-call, captured or parallel
kernel arm was added. Repro-only admission and the existing per-call compaction
gate are unchanged. Sibling OFF does not disable the independent storage rewrite.

## Evidence distinction and expected storage

The accepted preimplementation census observed the exact early tuple topology
and a default baseline with Bool mask, compact values/cardinality index, and two
fold scans. Positions were ALREADY elided. See C03-census-results.md and review.

The new assertions separately exercise:

| Arm | Required native route | LLVM pool assertion |
| --- | --- | --- |
| compaction ON, sibling ON | No mask/selected stores; joined two-accumulator traversal | 1 required input x (6x8) |
| compaction ON, sibling OFF | Same storage elision; two guarded scans | 1 required input x |
| compaction unshared, sibling ON | Existing positions-elided baseline; materialized mask/values | 3: x, Bool mask, compact values |
| compaction fully OFF (0), sibling ON | Legacy materialized reference, including positions | 4: x, mask, positions, compact values |

`unshared` is the fair storage comparison to the observed default baseline.
Fully OFF restores positions in the existing LLVM fallback; the card must not
take credit for that extra difference. Source anchors: `emitCompound`'s no-plan
COUNT/FILL/GATHER path in EmitLlvm.fs, versus `emitFusedCompaction` with
`PositionsNeeded=false`. C++ assertions also verify the positions-elided mirror.
These are assertions awaiting execution, not newly observed emitted results.

The stored source remains required (8N bytes). Expected removal is the N-byte
Bool mask, selected-value backing/slack and selected index object. No new
positions-array saving, measured peak-live bytes, bandwidth or speed is claimed.
The joined logical graph retains the pure predicate in EACH leg (two evaluations
per source cell absent downstream compiler CSE). Predicate sharing and a single
source load are not claimed. The interpreter evaluates independent nests and
establishes semantics, not native traversal count.

## Witnesses

OptimizeTests adds exact raw/rewrite tuple topology, preserved host identity,
two original seeds, fresh unlicensed guard arrows/origins and false-branch
identity; actual final joined IR; C++ one/two scans and seeds; actual LLVM
one/two literal-bound scans, allocation counts and per-leg predicate graph.
Failures report the actual counts. Existing C01 tests remain in the optimize gate.

Interpreter ON/join, ON/sibling-OFF and fully-OFF arms require admission/refusal
separately from independent pins: ordered `(13,-1868)`, empty `(7,-2)`, stored
alias, zero input, cancellation `(1,-3)`, and empty positive/negative zero seeds.
They compare stdout/stderr/status as well as pins. Both evaluators consume
optimized IR, so agreement alone is insufficient. Paired predicate/fold failure
requires the completed mask's `late_selected_predicate` failure first.

Source and synthetic refusals cover extra mask/keyed/alias/capture/third demand,
separate bindings/dependent seeds, selected maps/unseeded folds, adjacency/write/
abort, failing kernels/seeds, unknown calls, scalar capture/parallel metadata,
printing/gates, declared/removed/operand types, reference-equal other hosts,
constant execution payloads, lifetime events, structured/dynamic source and
foreign selection/mask references. These keep the original stores.

New corpus 547-553 provides ordered, signed-zero empty, cancellation, paired
predicate/fold failure, demanded selection, intervening-abort and zero-input
controls. The cancellation corpus uses a source `where repro` addition kernel
so its default licensed-materialized run also has a stable independent pin.
Demanded sample is `y(2)` (original coordinate 2, selected value 1), not a compact
ordinal lookup. Unset print retains producers in ordinary corpus runs; custom
result-only runs establish this card's positive route.

## Exact serialized runner request

Run from this private worktree; pin/restore environment per arm and save full
stdout/stderr, exit codes, actual backend provenance, source/binary hashes and
pass/fail/skip totals under `bench/.build/C03-runner`. Preserve failures; do not
edit source or regenerate pins. Root reviews before any repair. Staging:

1. `dotnet build Blade.fsproj -c Release`, then
   `bin/Release/net10.0/Blade.exe test optimize`. STOP on an unexpected build or
   optimize failure. The optimize block runs all C03 IR/emission/interpreter/
   refusal assertions and C01 regressions; no native toolchain is invoked by it.
2. After root accepts that gate, run each new corpus 547-553 through actual C++
   and actual LLVM with `Blade.exe run <absolute-source> --verbose`.
   Pin `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`,
   `BLADE_OMP_THREADS=1`, `BLADE_PRINT=result`, and `BLADE_LLVM=off/on` separately.
   Three independent arms: `(BLADE_COMPACTION_FUSION=on, BLADE_SIBLING_FOLDS=on)`,
   `(on,0)` and `(0,on)`. Check independent pins and normalized outputs/status,
   not only mutual agreement. Require positive admission in the ON plans and
   decline in unsafe controls. Any LLVM-to-C++ fallback is NOT an LLVM pass.
3. In 550, require `late_selected_predicate`/its source site, not
   `early_selected_fold`; in 552 require `intervening_selected_abort`, not
   `later_selected_fold`. Compare failure site/frame and status in all arms.
   Run 551 additionally with `BLADE_PRINT=result,y,sample` and require y
   `[1,3,2]`, sample 1 and result `(13,-1868)`; its selected stores must survive.
4. Retain CLI `emit` and `plan --json` artifacts for 547 in the three native
   arms AND the `unshared/on` mirror. Check actual required input retention,
   removed mask/value stores, traversal and predicate dataflow independently;
   reference positions are present only in the fully-OFF comparison. Retain
   demanded/failure control plans/emissions to establish refusals, not a win.
5. Root may request bounded interpreter/opt-diff corpus harness gates after
   these focused arms pass. Those generic gates can clear print environment and
   therefore exercise the demanded fallback. The optimize block's explicit
   interpreter ON/OFF checks and stage 2's native arms are the positive
   optimizer differential gates; do not replace them with default-print agreement.

No full/default suite, broad LLVM facts/goldens rerun, sanitizer or timing run is
needed solely for this tree-only card. No result in this note is a test pass.

## Frozen working-file SHA256

| File | SHA256 |
| --- | --- |
| src/Optimize.fs | `96452127C6A298BC4C2AB2B8DEC81BF4DCC8B968F1C4F890DF42ACA613F0E36A` |
| tests/OptimizeTests.fs | `405200D5CFFA71258864FFBA50BEF0E61986C0FA7492FE7C433428EE5FD3CC07` |
| 547_selected_tuple_two_seeded_chains.blade | `A757B2A709410CFF750BA515AE9BF57A0B97B315848FEEF13BC181A2D9BDE9FD` |
| 548_selected_tuple_empty_seed_bits.blade | `2CA7155FF97088DBBA19FCDB60A48D7F9229BE1F5361754B7B0FFABEC2F89C07` |
| 549_selected_tuple_cancellation.blade | `886CB7257D56ACEC6C203EA55CA22883B075EF4F01B70C266DC9B8E50A785BBB` |
| 550_selected_tuple_predicate_failure_first.blade | `4A9AFC2AF901D51A628F508FD26DF7A85847AF40FEB0CE1AC757815960B1ADBB` |
| 551_selected_tuple_demanded_selection.blade | `CDDBAD1EECC47822C4DBB187255E5A634ABA5CEA5F581D34ED01C4A98DD1B41A` |
| 552_selected_tuple_intervening_abort.blade | `F1774F2B5E91FBE5FE068CC34C4D6DFACF09F030EA420A143FEB9D70F6CB71F0` |
| 553_selected_tuple_empty_input.blade | `6386B50D01E2F10AFB623A1EB7B855BCA8E6F796600C5FB745A7C30A64B4AC2D` |

Corpus paths are under `tests/corpus/index-types/`. Handoff's own hash is
reported externally to avoid self-hashing. Static `git diff --check` passes;
tracked files retain LF index/CRLF worktree convention. Source is frozen until
root dispatches changes; implementation remains unvalidated pending Luna.
