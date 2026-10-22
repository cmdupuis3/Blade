# A05 sole-Luna default-schedule topology request

Root reviews/freezes this bundle before dispatch. No production or test-harness change. Read `../A05-census.md`; verify `manifest.json` and the approved compiler provenance. Source lane HEAD is `0f3c5cdb2e7b35afc54bc5603cf12f6ec4849320`. Root may select an already-built byte-matching binary; no build is requested by this census. If no matching approved binary exists, stop and ask root to queue one. Never silently select an older DLL.

Use the lane root, private TEMP/TMP and a fresh `bench/.build/A05-runner` output, one serialized process at a time. Record absolute DLL path/hash, source/probe/manifest hashes, compiler/source commit, every command, environment, stdout/stderr, exit code and output hashes. No build, generated-code compile, executable run, tests, interpreter execution, sanitizer, benchmark, golden regeneration or pin generation in this request. Full-suite count stays zero.

## Stage 1: default positive lower only

Set `$a05Dll`, `$a05Probe`, `$a05Input` and `$a05Arm` to reviewed absolute paths. `$a05DllSha` is the SHA256 of **the root-approved matching DLL**, not an inferred source hash. Run exactly this shape using the established .NET10 FSI toolchain:

```powershell
dotnet fsi "--reference:$a05Dll" --exec $a05Probe $a05Input $a05Arm 1 $a05DllSha lower
```

Input is `01_different_extent.blade`; output is a new `01-default-reuse1-lower` directory. The probe clears every optimizer gate to its compiler default, then pairs only BLADE_POOL_REUSE. In particular BLADE_FUSION/CSE/HOT_ROW/CELL_EPILOGUE/EPILOGUE_LEGS/COMPACTION_FUSION/COMPACT_ROW_CLASSES stay **unset/default**, not zero. It pins strict FP_REPRO=1/FP_CONTRACT=off, BLAS=off and one thread, with all outputs required (BLADE_PRINT unset), cache off and no instrumentation. Save the resulting explicit environment file. No isolated-scheduler or fusion-off arm is authorized.

Inspect `topology.txt`, `scheduler-owners.txt`, complete donor maps, diagnostics and lowering decisions. Check the ordinary function and 7/11 types, one retained fresh computed p, sum and in-bounds p(3) snapshots and completed bias, independent stored y tail, captures/metadata and actual planner owners. Verify caller bindings/IR still demand x0/y0/x1/y1/a/b and their later sums. These source outputs cannot be narrowed to checksums.

**Stop before further emissions** if lowering/validation fails, p is absent/deferred, the call ABI changes, another schedule owns the producer/observations, a snapshot still hides array evaluation, or expected ownership/metadata is unclear. Return exact artifacts and classify existing-route/no-gain or prerequisite; do not adjust fixtures or gates. The unexecuted probe may need a type/API repair: return the exact error to root, do not patch it or bypass the checks.

## Stage 2: paired positive emission

Only if Stage 1 establishes the intended ordinary shape, rerun the same probe with `emit` instead of `lower` into fresh `01-default-reuse1` and `01-default-reuse0` directories (reuse argument 1 and 0 respectively). Compare reuse1 topology, owners, donor maps and environment against Stage 1 before interpreting results. Every arm lowers once, validates once and emits C++ followed by LLVM from that same tree without a second lowering/table reset between renderers. Two lowered arms, four raw backend emissions. An explicit LLVM refusal is evidence to report; no fallback substitution.

For each backend, provide a concise numbered excerpt/trace tying function, source p ID, exact actual p backing/size, tracking/owner record, scalar fold read, p(3) read, completed bias, independent final 11-cell allocation, y read/final fill, p-specific free, returned root and call sites. Confirm whether p free follows **both** final allocation and fill. LLVM trace must follow the slot from allocation-root store to loaded pointer passed to blade_free; any-free/global-count evidence is insufficient. C++ trace must tie actual p data to the deallocator and show escape/suppression status. Separate body sites from two dynamic calls, payload buffers from extents/allocas, allocator free calls from OS release. Preserve raw .cpp/.ll, including arithmetic operands/flags and declaration attributes. No graph-identity claim from opcode counts.

If p is already freed earlier or no separate p payload exists, stop as existing-route/no-gain. If actual scope/root ownership differs, stop with that precise prerequisite. If expected p/result overlap is present, report the candidate boundary after completed bias and before final allocation; this is still not an implementation authorization.

## Stage 3: two fixed exclusion controls

After Stage 2 matches the predicted route, run `02_caller_owned.blade` and `03_later_read.blade`, default reuse1, mode `emit`, each into a new output directory. Two more lowered arms and four raw backend emissions; total maximum **one lower-only + four lower/paired-emit processes, eight raw backend emissions**. No reuse0 negative matrix or larger census is requested.

Caller-owned: trace formal pointers, final fresh result and absence of a function-local p ownership/free. Preserve caller arrays/results after the second call. Later-read: locate actual result materialization and later p(6) read, exact backing(s) and tuple expansion/copy/cleanup disposition. If default optimization moves the observation or changes the shape, report what it does; do not claim this source proves a read-after-allocation negative when raw emission shows otherwise. Tuple behavior is outside the proposed ordinary return implementation; no cleanup repair here.

Return artifacts/hashes and a short table per arm: retained/owned p, actual last payload read, result allocation/fill, exact current p free, returned owner, scheduler/donor owner, refusal/error and source-snapshot disposition. Root reviews topology before any production work or native validation. Analytic EXPECT comments remain unvalidated; no test pass, capacity/performance win or broader PM02/family closure follows from emission.
