# C02 frozen handoff: PM10 private rank-2 map to seeded row folds

Status: implemented, awaiting root review and sole Luna runner. No build,
lowering, emission, compilation, execution, test, oracle or benchmark was run
by this coding lane. Static inspection used `git diff --check` (clean) and
confirmed the two edited F# files retain index LF / working-tree CRLF.

Checkout: `.round4-worktrees/c-terminals`, branch `codex/round4-c-terminals`.
Parent checkpoint: `c764b6ddf888b4368538c18e363c86f2f579da1e` (accepted census).
No implementation commit, push or merge has been made.

L1 prerequisite update: the successful C02 validation later exposed an existing
LLVM failure-order bug in corpus 036 (both ON/OFF reached the fold first).
`C02-L1-handoff.md` now describes the approved semantic repair and current
manifest/request. With that repair, the LLVM reference correctly allocates
three pools OFF; C02 ON still needs only M + rows. The earlier two-pool OFF
observations below are historical baseline evidence, not the repaired reference.

Initial runner build of handoff `D107518B...F41884` failed with three FS0001
errors in `tests/OptimizeTests.fs:1606`–`1608`: omitted list separators between
physical lines made three witness entries parse as three-tuples. The log is
`bench/.build/C02-runner/build-D107518B.log`. No tests ran and the production
route remains unvalidated. The first correction (handoff `50288EB2...0817C8`)
still failed with FS0003 at line 1606: adjacent parenthesized pairs at that
nested indentation parsed as function application, with downstream FS0001
inference errors choosing TypedLowerEnv for `md`. Its log is
`bench/.build/C02-runner/build-50288EB2.log`; no tests ran. The second correction used a
separate explicitly typed `(string * Blade.IR.IRModule) list` and explicit
semicolons between every pair, then iterates that list. All ten refusal
witnesses remain. Production source and corpus bytes are unchanged. The sole
runner rebuild then succeeded (`build-184AF459.log`), and `test optimize`
reported 560 passed, 1 failed (`optimize-184AF459.log`). The sole failure was
the unsupported expectation that LLVM OFF allocated a mapped matrix. The
runner's direct LLVM diagnostics prove OFF already streams that private
map. This refreeze changes only tests/docs: both private LLVM routes require
two exact pools, and the demanded P control requires three in both settings.
Assertions report actual counts/layouts and check evaluation dataflow and
required print calls. Production source and corpus remain unchanged; no new
runtime pins are generated. These corrected assertions still require runner
rebuild/retest. No compiled program execution is established by these emissions.

## Observed missing route and bounded implementation

The accepted census and runner logs in `bench/.build/C02-runner` settle the
initial shape. The explicit scalar Float64 single-matrix map in
`C02-census-inputs/01-primary.blade` becomes a private `IRLet` rank-2 apply,
then an immediate row apply whose one-parameter kernel directly reduces
that row with literal seed 7. Existing C++ code emits M, the full mapped matrix,
then rows. The parenthesized rowwise idiom streams through the existing
`IRReduceCompute` and emits only M and rows in C++; its actual LLVM emission
has exactly two `blade_alloc_cells` calls. Later direct LLVM baseline emission
confirmed the original private form already has those same two pools; C02
preserves existing LLVM streaming and adds storage elision in C++ only. The
distinct two-vector outer
product is already streamed by `TypeCheckInfer.partialFold.outerProductRewrite`
and is not implemented again here.

`Optimize.fusePartialRowTerminalsModule` admits only that whole initializer:
one scoped private rank-2 Float64 scalar map over a dominating stored dense
top-level input, immediately consumed by its sole explicitly literal-seeded
rank-1 row fold. It rewrites into the accepted rowwise machinery. It does not
recognize generated name prefixes; it matches local and callable IDs/scopes.
Named top-level producers remain materialized/observable under default print.
No output hiding or explicit print selection is needed for the positive arm.
The pass runs after elementwise fusion in `optimizeModule`, before IRLift,
CSE, pool reuse, hot-row, membership and compaction planning. Those tables
are derived from the resulting tree, so no keyed planner fact is orphaned.
The new callable receives normal subsequent forcing/qualification/lowering.
No ownership event, renderer, interpreter operation or IR variant is added.

Gates are per-call `BLADE_FUSION` and `Toolchain.fpReproEnabled()`. Only the
reproducible serial graph is admitted. Default licensed arithmetic is
unchanged. The fresh row callable is unlicensed, with a freshly constructed
plain `mkFuncArrow`; existing map/fold callables and their exact argument order
are retained. Under the required reproducible policy their existing terminal
renderers retain ascending row/cell order. Seeds remain once per original row:
C=0 returns the literal seed without map/fold steps, and R=0 runs no row seeds.

The original row parameter uses an abstract inferred column extent in the
census. Its replacement is built from M's exact concrete C record and a fresh
row parameter ID. Inner Apply/MethodFor metadata uses that concrete rank-1 C;
outer apply metadata uses M's R as S and C as T. Crucially, the original
consumer OutputType is retained verbatim: its R record has a different ID
from M's R even though the tag/extent agree. The fresh callable is inserted
after its original row callable and records `DerivedFuncOrigins` to that row.

## Legality and complete demand checks

The stored-input proof follows existing alias chains and rejects deferred,
virtual and provider sources. Source/alias initialization payloads may not
write. The consumer must be an ordinary host with no side-table execution
payload or provider action; even an unrelated constant side operand declines.
Map/fold/callee graphs must pass `IRTotality.auditCallable`, the existing write
analysis and an additional closed-reference audit. Only parameters, local lets
and recursively audited known callees are permitted. Captures/global reads,
callbacks, recursion, unknown callees, writing, integer-failure operations,
unaudited math, static/arity/device/parallel strategies all decline.

`compactionRoots` provides the complete module/program-callable census:
binding values, host type, side operands, callable bodies/signatures/captures,
declared types and provider source/name operands. Only the actual host binding
ID gets its matched expression subtracted. A reference-equal expression object
on another binding still counts as a full demand. The matched scope itself
must contain precisely the two expected private references (actual consumer
array and MethodFor provenance) and no private reference in embedded types.
Any remaining alias, type, capture, side payload or hidden observer declines.
This scoped local ID cannot be imported/exported as a module binding; callable
roots cover an illicit escaping capture. Name-only provider masks are refused
conservatively since local names cannot be certified from IR.

The omitted matched expression is certified separately, including hidden
metadata: static nonnegative rank-1 plain unsymmetric axes without dependencies;
identical tag/extent provenance; F64 nonvirtual ArrayTypes; no shared/kernel
axes, co-iteration, Reynolds or structure strategy; exact two-S producer and
S/T consumer roles; exact state/triangle/SDim/rank lists; both MethodFor arrays
by ID with identical identities, raw two-S geometry and no shared axes.
Producer `tilePlainInfo` is deliberately not used: its per-operand state-count
gate excludes this one-operand two-axis producer (the census has two states).

## Owned files and focused witnesses

- `src/Optimize.fs`: one early tree rewrite and one pipeline call.
- `tests/OptimizeTests.fs`: C++ producer-pool counts (2 off, 1 on), actual LLVM
  private allocation layouts (M=6x8 bytes, rows=2x8 bytes ON; the L1-corrected
  OFF reference additionally has a 6x8 mapped input), and
  demanded layouts (M=6x8, P=6x8, rows=2x8, both ON/OFF), all in main. The
  LLVM checks trace dynamic evaluation loads/stores through pool/row pointers,
  scalar map/fold operations, exact seed and actual required print calls;
  failures report actual counts and layouts. Exact R/concrete C/origin metadata, interpreter
  ON/OFF with independent sums, ordered folds, transitive scalar block, C0/R0,
  cancellation and negative-zero seeds. Refusals cover named/printed/keyed or
  deferred producers, alias observers, captures, may-fail producer/fold/seed,
  nonliteral seeds, hidden writes and parallel strategies. Synthetic probes
  cover demanded declared types, captures, unknown callees, structured source,
  another binding sharing the same expression object, Apply/MethodFor/shared/
  kernel-axis payloads, and a constant host side-execution operand.
- `tests/corpus/sql-reduce/031`–`036`: sum + downstream R-typed consumer,
  noncommutative ordered/transitive block, zero columns, zero rows + R-typed
  consumer, named printed/keyed matrix, and paired failure precedence.
- `bench/notes/round4/C02-census.md`: post-census correction identifying C++
  as the missing storage route and LLVM as existing-streaming parity.

The paired failure control's producer fails only at v=6 in row 1; its fold
would fail on the first cell of row 0 if streamed. Both ON and OFF must report
the producer's integer divide BL8013, source site and `late_producer_failure`
frame. The in-process interpreter pin compares full stdout/stderr/status and
requires that frame while excluding `early_fold_failure`. The corpus pins the
same error/frame. Default-suite ordered pins use a noncommutative fold, so they
are robust without relying on simplification of multiplication by one. The
cancellation/signed-zero optimized-route witnesses explicitly pin repro and
contraction-off inside the unit block.

## Exact serialized runner request

Use this exact frozen checkout or a root-approved identical snapshot. Run
commands only in the sole Luna lane, with private working directories and
saved stdout/stderr/exit statuses. Do not run the full/default suite.

1. `dotnet build <checkout>/Blade.fsproj -c Release`; record full binary/source
   hashes and build diagnostics. Compilation must succeed before testing.
2. `<checkout>/bin/Release/net10.0/Blade.exe test optimize`; record full totals
   and every `partial_rows_*` result. These cases explicitly set repro/default
   print and compare fusion ON/OFF, so both optimized and original IR are
   exercised despite interpreter and backends otherwise sharing optimized IR.
   The LLVM allocation test must use actual `EmitLlvm.tryEmitProgramNamed`
   success, with no C++ fallback credited as an LLVM pass.
3. Run `test sql-reduce`, then `test interp sql-reduce` and
   `test opt-diff sql-reduce`, in separate private working directories. Record
   all pass/fail/skip totals. These category gates provide regression coverage;
   their environment scrub may clear repro, so do not credit an inert route as
   positive C02 validation. The custom optimize block is the positive IR gate.
4. Independently run corpus 031–036 through the real C++ backend twice each:
   set `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`,
   `BLADE_LLVM=off`, remove `BLADE_PRINT` (default all), and use
   `BLADE_FUSION=on` then `0`; invoke `Blade.exe run <absolute-file>`.
   Compare stdout/stderr/status to each other and to the independent pins.
   For 036 compare the source-site/stack first-failure details, not only BL8013.
   Compiler/toolchain chatter may be separated from program output explicitly.
5. Under the same strict flags, emit/plan the primary and named control with
   `Blade.exe emit <file>` and `Blade.exe plan <file> --json` for each fusion
   setting. Positive ON must show `partial-row-fold` applied and a new row
   terminal, no mapped rank-2 allocation/fill/read; required M and rows remain.
   Both corrected references OFF must have the extra mapped pool. Before L1,
   LLVM OFF had only two pools but violated the may-fail producer boundary;
   that unsafe baseline is not the post-L1 reference.
   Named 035 must retain P's full pool,
   print and keyed sample in both settings, with a refusal decision.
6. Repeat 031–036 runtime checks with `BLADE_LLVM=on`, confirming actual LLVM
   selection, then independently compare with C++ and original IR. Preserve any
   explicit unsupported backend refusal or toolchain skip; do not silently
   treat fallback as an LLVM runtime pass. C0/R0 particularly require runtime
   evidence beyond their previously successful census emissions.

No timing or memory benchmark is requested at this checkpoint. The expected
C++ storage change is one private mapped `8*R*C` pool plus its row-pointer metadata;
the required stored M and `8*R` result remain. Allocation curves, peak memory and
runtime improvements are unmeasured. LLVM's historical two-pool OFF behavior
was unsafe for may-fail row inputs. L1 restores that boundary; C02 ON safely
elides its mapped pool relative to the corrected reference. This does not
constitute a memory improvement over the historical unsafe implementation.
Existing C01/census runner evidence is
separate from validation of this new frozen implementation.

## Frozen working-file SHA256

These identify the exact pre-run bytes (F# files are CRLF in this checkout).
The handoff's own hash is reported in the freeze message to avoid self-hashing.

| File | SHA256 |
|---|---|
| `src/Optimize.fs` | `45EDD5C6E608683811FFD9268D2D5287479E12654C85F6054C089F4D59B8FA7D` |
| `src/EmitLlvm.fs` (L1) | `C1E5B9C68A5F1F1A4CAE86496A96B9ECECA0CAD094F1E97FCCC391F661D36FF3` |
| `tests/OptimizeTests.fs` | `B9CE61BA238A0D04A071625DAB4475526ADAA138F6FE6D146571A1AC4B43FA47` |
| `bench/notes/round4/C02-census.md` | `A91A93B9DFCB5150CF763E8F88EE579B8DBDB91A438590B3C644F71A3A756EF9` |
| `bench/notes/round4/C02-llvm-order-census.md` (L1) | `666864EE139BA7A50C113ACE6041F85F0CDEA78B5F1758413C43C2204312B3A1` |
| `tests/corpus/sql-reduce/031_private_partial_map_seeded_rows.blade` | `59C26083962399AFA584B829629BE8F1DDEBE225B3B053E12C49CEBF6B38AC5D` |
| `tests/corpus/sql-reduce/032_private_partial_ordered_rows.blade` | `3C74FFF3CB99EBF120AC144736CEBA0A165E3438D675C35282A4084E5701258D` |
| `tests/corpus/sql-reduce/033_private_partial_zero_columns.blade` | `4DB5A4FB85B0A1CF4AC0E7A7A104A595E465DB72909D0C3F4843A6313B800B22` |
| `tests/corpus/sql-reduce/034_private_partial_zero_rows.blade` | `9BE0D499C112A74BA00FE62335FA399C349EB8389F99C1D510ABF501746124ED` |
| `tests/corpus/sql-reduce/035_demanded_partial_map_retains_matrix.blade` | `0B6A537AA8345AF50F0BA205EDEC8FDCDAA928B80C7FCA879578E0B260D240D8` |
| `tests/corpus/sql-reduce/036_private_partial_producer_failure_first.blade` | `DE4ECE687ACD7F090D97A2211EFC929A6EDF6D5A5395CC47F69F08D2199ACC15` |
| `tests/corpus/sql-reduce/037_private_partial_producer_before_seed.blade` (L1) | `096C0BE420513EAC51511985C6616C1C879EA266C53E875F99422A02997BD019` |
| `tests/corpus/sql-reduce/038_private_shared_partial_source.blade` (L1) | `3B1A60352E4E97BEA529F477C3249B86056970B6883024A3C8CE446604E75BB3` |
| `tests/corpus/sql-reduce/039_packed_row_argument_access.blade` (L1) | `E7A0713C47923AA9DD185D88553C5A9312E3D2FCB8C369C2B11ECB21C9CDBB1B` |
