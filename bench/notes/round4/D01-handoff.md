# D01 frozen candidate: rank-1 Zarr to shared scalar joined terminals

Status: implemented, statically reviewed by lane D. The first runner build
failed in test-fixture syntax; the repaired candidate is **not yet rebuilt**.
No category, emitted route, executable, or memory/timing result is validated.
One kernel only. No commit, merge, Claude/PAL call, delegation or full-suite launch.
Base: master `361504b6`; branch `codex/round4-d-streaming`.
Checkout: `.round4-worktrees/d-streaming`.

Runner return 1: `bench/.build/D01-runner/build.log` reports 61 F# parse/offside
errors, beginning with FS0588 at tests/ZarrTests.fs:1150 and FS0010 at :1159.
The unindented multiline format-string terminator plus same-line format
arguments broke the nested fixture helper's offside context. The remaining
errors cascade through the file. Lane D read the complete diagnostic list
and the entire new test block, replaced only the source-text constructor with
an indented String.concat list, and preserved all test operations/assertions.
This changes no emitter implementation or intended Blade fixture program.
No build, compiler execution or test was run by the coder. Request Luna's
serialized Release rebuild and standalone Zarr gate against the hashes below.

## Source census and scope

The direct rank-1 streamed scalar fold is a separate path in
`src/CodeGenBinding.fs` (`streamedFold` inside `genReduceBinding`).
The existing explicit reduction join already constructs named shared scalar
producer leaves and independent fold wrappers/seeds, but calls
`streamedNestSetup` and the fused fiber emitter. `genElementBindingStreamed`
in `src/CodeGenLoopNest.fs:530` refuses `resultRank <= 0`. Thus the source
form below previously reaches a real missing scalar provider route; the
ordinary materialized map wrapper is not a bounded terminal implementation.

```blade
import zarr as z
let sample = z.load("store")
let A = sample.vars.A |> z.stream
let y = method_for(A) <@> lambda(x) -> x * 2.0 + 1.0
let total, squares = object_for(<&!>) <@> (
    reduce(y, (+), 7.0),
    reduce(method_for(y) <@> lambda(v) -> v * v, (+), 3.0))
```

This candidate connects that existing sharing representation to a new block
consumer. It does not rewrite IR, omit a resident binding, manufacture a
count leg, or alter sibling-fold planning. Interpreter/LLVM provider support
is unchanged and is explicitly refused by positive fixtures' backend probes.
Source reachability and the emitted route are asserted in the new tests;
they remain unverified until Luna runs them.

## Owned files and insertion points

- `src/CodeGenBinding.fs`: `tryGenRank1StreamJoin` immediately before the
  atomic `genBinding` rec-chain; one interception in `genReduceJoinCore`
  immediately before `streamedNestSetup`. Existing resident/fiber paths remain
  the fallback when this is not a rank-1 streamed candidate.
- `src/CodeGenState.fs`: one context-owned `StreamTerminalModule` option;
  empty contexts have no whole-program evidence.
- `src/CodeGen.fs`: private `genModuleCore` / `genModuleSplitCore` take an
  explicit whole-program bool. Public low-level wrappers pass false. The
  private self-contained assembler passes true after all IRProgram modules
  have been merged. No ambient mutable provenance flag.
- `src/providers/ZarrProvider.fs`: factor `genStreamWindowCore` with an
  optional whole-read tally; existing `genStreamWindow` passes None. Add
  `rank1TerminalChunk` capability and `genStreamRowsTerminal` adapter.
- `tests/ZarrTests.fs`: `streamedJoinedScalarsE2E` in the standalone Zarr
  suite, directly after existing streamed-fold tests.
- This handoff.

No Optimize/Lowering file is modified. A01 touches nearby CodeGen files on
its checkout; merge only these D01 insertion points.

**C01 integration cleanup:** D01 temporarily supplements `compactionRoots`
with local `declaredTypeDemand`, because base compactionRoots misses
`modul.Types`. C01 adds those roots to the shared helper. When combining the
cards, remove D01's local declared-type supplement and use the now-complete
shared roots once. Rehash the combined sources before Luna validates them.

## Admission and semantics

Admission is complete before code emission and before entering the legacy
fiber reader. It requires one known local uncompressed dense rank-1 Float64
Zarr v2/v3 variable, matching static shape, exactly one named shared producer,
two independently seeded Float64 addition chains, a serial host traversal,
literal seeds, and closed total scalar arithmetic. The whitelist admits
literal Float64 +/-/*, negation, local lets, and closed nonrecursive calls;
it also applies IRTotality's callable audit. Captured state, subscripts,
casts, effects and unsupported arithmetic decline.

Licensed fastest fold trees require `BLADE_FP_REPRO=1`; requested omp/cuda/mpi
folds are outside this card. No reassociation or block partial sums are
introduced. The existing callable wrappers and map renderer preserve the
source arithmetic and existing contraction policy. `ReynoldsResult` contains
only CppExpr and two statistics, with no prelude/statement payload to drop;
Reynolds itself is refused. Each leg keeps its original seed and wrapper.
The logical coordinate is `lo + q`; empty input leaves both seeds unchanged.
Odd tails use `min(B, N-lo)` and cursor advancement cannot overflow N.

The whole-program census uses compactionRoots plus embedded/type extent
walks, rejects source/producer demands outside the region, explicit producer
print selection, foreign-module consumers (all modules are merged), hidden
operands, provider writes, other reads, mutable array state, and old storage
planner overlap. Actual `modul.Bindings` positions establish source dominance
and the intervening statement interval; numeric IDs are not ordering facts.
Uncertified intervening statements decline. Direct low-level genModule calls
lack whole-program provenance and decline this route.

The Zarr adapter reuses the existing chunk-key/read/fill/short-read machinery.
It tallies missing values with global flat coordinates across every block,
then emits the existing whole-read missing_report after all reads and before
publishing the tuple. A later short chunk still wins over a missing value in
the first block, matching materialized .read. Scalars remain private while
validation is incomplete. Original ordinary window/fiber readers retain
their existing validation boundaries.

## Storage model and limitations

For chunk edge B, the simultaneous tracked payload is B*8 converted input
bytes plus B*8 padded disk chunk bytes, two Float64 accumulators, one shared
scalar, cursor/bounds, missing tally and fixed reader descriptors. The
decoded vector is recreated per block and coexists with input while consuming
that block. The input vector is scoped to the terminal and released before
tuple publication. No full output/map, run-offset table, row-pointer skeleton,
chunk manifest, asynchronous queue or decoded cache is introduced. Metadata
is baked into fixed shape/chunk constants and one path/key at a time.

This is a payload model, not measured RSS or a whole-process allocator bound.
iostream/file-system/path allocations and compiler-time metadata caches are
outside it. N and padded chunk bytes are conservatively capped to the
existing window reader's Int32 arithmetic. No compressed codecs, integer or
Float32 maps, count state, multilevel maps, multiple providers, source replay,
budget enforcement, sinks, parallel lanes or runtime external-store mutation
guarantee is claimed. Zarr's existing compiled metadata/source-stability
assumption remains; this card does not add snapshot pinning.

## Requested Luna validation (all unrun)

1. Build this exact frozen tree (Release). Do not launch the default/full suite.
2. In a private working directory, run the standalone `Blade.exe test zarr`.
   Report pass/fail/skip totals, toolchain identity and the new route assertions.
3. New witnesses cover v2 zero/11/257 and v3 11 cells at B=4; materialized
   .read and .stream use identical BLADE_PRINT=`total,squares`, repro=1,
   contraction=off. Closed forms independently pin both seeded answers:
   N=11 -> total=150.0, squares=2302.0; N=0 -> 7.0 and 3.0.
4. Refusals cover fallible conversion, source indexing/capture, demanded
   resident producer, explicit producer print, extra provider read, mutable
   state, and licensed fastest mode. A required resident producer may retain
   its existing materialized fallback, but must not claim the new route.
5. Missing fixtures place NaNs in first/last blocks and require one report
   counting two cells, first [0], with no scalar publication. A paired fixture
   truncates final chunk to one byte; both .read and .stream must report its
   expected-32-byte short read before BL8015. The stream marker must be present
   even in failing runtime witnesses.
6. Authored v2/N=11 provenance assertions explicitly generate the normal
   whole program, then call direct public genModule and genModuleSplit: both
   must omit the marker and refuse unavailable whole-program demand facts.
   A subsequent split-timing self-contained assembly must emit both the route
   marker and its setup timing checkpoint. The prior timing flag is restored
   in finally, including failure paths. These assertions are unrun.
   Repeated compiler calls with changed FP environment also must not reuse a plan.
7. Inspect emitted stores/allocations: exactly one shared scalar evaluation
   per cell, independently seeded uninterrupted fold chains, no complete
   source/map pool or N/B schedule table. An unchanged optimizer differential
   does not establish route coverage. Provider .stream interpreter/LLVM
   probes must refuse explicitly; .read is the supported reference.

No timing or memory win is claimed. Runtime scaling beyond the route/storage
assertions is a later runner measurement request, not work performed here.

## Frozen raw-file SHA256 (working-tree bytes)

| File | SHA256 |
|---|---|
| src/CodeGenBinding.fs | F4A41399274E94B23F3B5924C4BAD4BA4964DC1BCA9E7E7EAF7216B22FCE98D0 |
| src/CodeGenState.fs | 5FFDE77E98038C3DA4AC6E3B5D8966AC57AA2711D22EDC372362C999B01B4DDE |
| src/CodeGen.fs | 997E51D28C9EAA2BBFA8DC4AC7720DD4F7837413D5D08C6092FFD261C5A03338 |
| src/providers/ZarrProvider.fs | 76A5D488D0D73E7322E4540E48EFCE45FCE0E7279EFACD7926CB0B1127B26EC6 |
| tests/ZarrTests.fs | A85ED13D07EAB93FF4AFC1B50A7DFA2637A500004F42D63C63D57C0C57FA33C6 |

Static checks performed: `git diff --check` clean; all five edited tracked
files retain index LF / working-tree CRLF, with focused diffs only. Graft
searched stream in src and returned no F# coverage (one unrelated indexed
file); targeted source reads supplied this census. No graft savings footer.
