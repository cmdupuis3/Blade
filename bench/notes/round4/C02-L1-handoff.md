# C02-L1 frozen handoff: LLVM row-input evaluation boundary

Status: implemented, awaiting root source review and sole Luna validation.
No worker build/lower/emit/compile/run/test/oracle/benchmark was performed.
No commit/push/merge. Checkout and parent HEAD remain C02's
`c-terminals`, `codex/round4-c-terminals`, `c764b6ddf888b4368538c18e363c86f2f579da1e`.

This is one bounded semantic prerequisite to C02 acceptance. Before L1, root
reported optimize 562/0, sql-reduce 36/0, interp 37/0, opt-diff 36/0 with two
skips. Actual LLVM runtime of corpus 036 then failed first in the fold (line
12) in both fusion settings, while C++/interpreter reported the complete
producer's failure (line 8). These earlier passes do not validate L1. The
original ABORT pin and C02 storage-elision production pass are unchanged.

The initial L1 frozen build (handoff SHA256 `4459DFE7...07BFA15`) failed at
OptimizeTests.fs:1623–1624 with FS0611/offside/unmatched-record syntax errors.
Evidence remains in `bench/.build/C02-L1-runner/release-build.stdout.txt`.
No L1 tests ran. The syntax-only repair separates the nested module/binding
map into a local `alteredModules` value, then constructs the program record;
the module lambda has an explicit IRModule annotation and a properly indented
body. Adjacent newly added test expressions were inspected for the same
pattern. Production source and all witnesses remain unchanged. This corrected
freeze still requires the sole runner's build and targeted validation.

The corrected L1 build passed; its optimize retry reported 580 passed, 4
failed (two controls, each ON/OFF). Evidence is preserved in
`bench/.build/C02-L1-retry-runner/test-optimize.stdout.txt`. The packed row
control reached the existing front-end compact-reduce refusal, so it now uses
the supported keyed row(0,0) read, with separate corpus row-value pins. The
supposed direct scalar control was actually an unannotated rank-2 full fold:
TypeCheckInfer.rankKDesugar (3715 onward, before fused-terminal inference)
binds its anonymous mapped matrix and indexes it from a serial IRForRange
nest. EmitLlvm.forceLoopBodyProducers (3223 onward) forces that named source
before the nest. Both paths precede L1; its map operand still scalar-peels M
and L1 leaves that emitArr path unchanged. Thus two pools are explained by
existing lowering topology, not new row forcing. No baseline rebuild is
needed to infer that unchanged source path. The refreeze separately pins that
mapped-binding/index/IRForRange topology and two pools, and adds a genuine
rank-1 scalar terminal pinned as IRReduceCompute without IRForRange and one
input pool. Production code and all earlier failure witnesses are unchanged.
These corrected controls still await the runner; no result is claimed here.

## Final change and proof boundary

`EmitLlvm.applyToArr` now prepares real array/row kernel operands with
materializeExpr before constructing the outer producer/nest, allocating its
result or evaluating any row seed. Whole-input materialization restores
formalism 2.5's method_for contract and interpreter/C++ behavior. It cannot be
deferred to a rowView or the inner fold: that would still reach row 0's fold
or seed before completing row 1's input producer.

Input preparation uses validated metadata before emitting an operand once.
Virtual multi-slot sources use IRStorage's parameter-span/start rule (one
scalar parameter per virtual axis); real arrays use one parameter. Negative
rank metadata, nonzero virtual-slot ranks, list/parameter mismatches and
array/scalar peel disagreements refuse explicitly. Source axis rank sums
index-record Rank, while concrete kernel input rank counts index records.
Both pre-emission and actual KArray binding check the same array/scalar choice;
the emitted operand's axis rank must agree with its source type. No emitArr
probe followed by a second operand evaluation is used.

An APool input is allocation/copy no-op. materializeExpr upgrades a named slot
so repeated row consumers share one backing input; existing forced-print,
statement-hoist and PoolScopes/keepPool handling remain responsible for
dominance/lifetime. Real operands prepare in original order. Captures,
consumer writes and aliases cannot bypass this boundary. Scalar-peel maps,
virtual scalar ranges/reverse and direct scalar IRReduceCompute keep their
existing paths. Specialized domain/ragged/compound routes are not expanded.

No new streaming certificate, context set, proof pass or totality helper was
added. C02's existing closed pure/total/demand proof is still its single
optimization licence. C02 ON already rewrites the outer input to stored M
plus an inner scalar terminal, so it retains two pools. The corrected OFF
reference now has M + mapped input + rows, three pools. Historical LLVM OFF
had two pools but violated failure order; this is not a measured memory win
over that unsafe implementation. Optimize.fs is byte-identical to its C02
freeze. The semantic barrier itself is independent of optimizer/FP knobs.

## Owned delta and witnesses

- `src/EmitLlvm.fs`: ordinary apply operand classification/materialization and
  matching actual KArray assertion; no new IR or runtime operation.
- `tests/OptimizeTests.fs`: corrected LLVM primary assertion (2 ON / 3 OFF),
  demanded P (3 both), actual evaluation dataflow/required prints and counts.
  L1 witnesses cover stored row source with no extra pool; private block-local
  deferred P consumed twice, no P keyed read/print, exactly one shared backing;
  a packed SymIdx<2> row keyed read (two scalar axes, one record rank);
  multi-slot range; virtual range/reverse; scalar map; a genuine rank-1 scalar
  IRReduceCompute and a separately pinned materialized rank-2 full-fold desugar;
  negative and inconsistent real/
  virtual metadata. Unsafe producer fill must complete before the row-result
  allocation, with actual pool layouts reported, in both optimizer settings.
- Existing 036 remains the paired producer/fold failure control.
- New `037_private_partial_producer_before_seed.blade`: producer failure must
  precede the first row's failing seed, preserving late_producer_failure.
- New `038_private_shared_partial_source.blade`: private P has two row
  consumers and both results are observed via scalar totals; hand-derived
  tuple pin (111.0, 83.0). This isolates named-slot upgrade from top-level
  print/keyed forcing. The existing demanded P control remains separate.
- New `039_packed_row_argument_access.blade`: supported packed row arguments
  read row(0,0), with independent values [1.0, 7.0]; it does not ask compact
  reduce to decide canonical-versus-logical folding semantics.
- C02 census/handoff corrected to distinguish the historical unsafe LLVM
  baseline from the repaired reference. The final cause/design note is
  `C02-llvm-order-census.md`; its superseded context-set proposal was removed.

## Exact targeted runner request

Use the exact frozen snapshot, private working directories and sole serialized
Luna runner. Root reviews the actual source before runner execution. No full
suite, oracle generation, timing or memory benchmark is requested.

1. Build once: `dotnet build <checkout>/Blade.fsproj -c Release`. Record binary
   and source hashes, warnings/errors and exit code. Then
   `Blade.exe test optimize`, recording every `partial_rows_*` result and full
   pass/fail/skip totals. It performs actual LLVM emission, private/shared
   allocation/dataflow/refusal checks and independent interpreter ON/OFF pins.
2. Run `Blade.exe test llvm facts` and `Blade.exe test llvm goldens`; no golden
   regeneration. These check metadata/attributes and detect unintended scalar
   path changes. Any pre-existing unsupported case/skip must be reported.
3. Run actual LLVM and C++ corpus 031–039 separately with BLADE_FP_REPRO=1,
   BLADE_FP_CONTRACT=off, BLADE_BLAS=off, default print, and BLADE_FUSION=on/0.
   Use `Blade.exe run <absolute-file>`, selecting BLADE_LLVM=on/off explicitly
   and verifying actual backend selection. Compare independent pins and both
   backends' stdout/stderr/status. In 036 require line 8/producer frame, not
   line 12/fold frame; 037 requires its producer line 7/frame and excludes
   row_seed_failure. Compare source sites/stacks, not only BL8013. The private
   shared-source 038 must return (111.0, 83.0) without printing local P; packed
   row-access 039 must return rows [1.0, 7.0].
4. Save primary/036/037/038 LLVM ON/OFF emissions and plans. Primary must be
   two required pools ON, three in the corrected OFF reference. Unsafe 036/037
   must be three both, with complete producer fill dominating row allocation,
   seed and fold. Inspect control flow/dataflow, not only allocator call count.
   Demanded 035 remains three both, with P print and keyed sample intact.
5. Target existing LLVM array/row argument regressions via raw run or a bounded
   runner selection: loops 085 (zip row prodsum), 168 (row-returning compact
   output), 181/191 (row views), 200 (per-row join), 206 (already-streamed
   two-vector partial), 212–215 (row arguments/forwarding), 472 (nested deferred
   map fold), 484–485 (capture/failure ordering), and sql-reduce 023–024.
   Preserve explicit unsupported renderer reasons; fallback is not an LLVM
   pass. Do not launch the entire loops/full suite solely for this repair.
6. Re-run the affected sql-reduce category and its interp/opt-diff gates after
   root accepts the focused runtime results. Those gates scrub environment;
   positive C02 repro-route validation is the custom optimize block and step 3.

Allocation predictions are source/emission assertions awaiting this frozen
run. No peak-memory, allocation curve or timing improvement is claimed.

## Frozen working-file SHA256

| File | SHA256 |
|---|---|
| `src/EmitLlvm.fs` | `C1E5B9C68A5F1F1A4CAE86496A96B9ECECA0CAD094F1E97FCCC391F661D36FF3` |
| `src/Optimize.fs` (unchanged C02) | `45EDD5C6E608683811FFD9268D2D5287479E12654C85F6054C089F4D59B8FA7D` |
| `tests/OptimizeTests.fs` | `B9CE61BA238A0D04A071625DAB4475526ADAA138F6FE6D146571A1AC4B43FA47` |
| `tests/corpus/sql-reduce/037_private_partial_producer_before_seed.blade` | `096C0BE420513EAC51511985C6616C1C879EA266C53E875F99422A02997BD019` |
| `tests/corpus/sql-reduce/038_private_shared_partial_source.blade` | `3B1A60352E4E97BEA529F477C3249B86056970B6883024A3C8CE446604E75BB3` |
| `tests/corpus/sql-reduce/039_packed_row_argument_access.blade` | `E7A0713C47923AA9DD185D88553C5A9312E3D2FCB8C369C2B11ECB21C9CDBB1B` |
| `bench/notes/round4/C02-census.md` | `A91A93B9DFCB5150CF763E8F88EE579B8DBDB91A438590B3C644F71A3A756EF9` |
| `bench/notes/round4/C02-llvm-order-census.md` | `666864EE139BA7A50C113ACE6041F85F0CDEA78B5F1758413C43C2204312B3A1` |
| `bench/notes/round4/C02-handoff.md` | `93F9064DFCB58C135B605698DD98243B5F8844A4B84CFA527DA5DC206489D389` |

C02 corpus 031–036 hashes remain in C02-handoff.md and unchanged. This
handoff's own SHA256 is reported in the freeze message, avoiding self-hashing.
