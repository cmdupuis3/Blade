# C03 topology census results

Status: bounded source-topology diagnostic complete; no implementation was made. The frozen integration checkout was `codex/round4-peakmem` at HEAD `4480ef455ad85864516ab257cf922d4df183f76b`. Its only pre-existing worktree change was the untracked design `C03-census.md`, whose SHA-256 was `166753B0C3EB04211618F1A064B97E9D57FF4A62C78496211D5EB7386E09F81B`. Release compiler identity: `Blade.exe` `C2C8AD8A175AD1CB775F6945FF4A00A0AB8150FC4F845027FD7FDE319B678034`; `Blade.dll` `ECFDBFF1432867E8DC1DD82A5AEAFE172B57E4729F488C2F70BA4096E251E786`.

`dotnet build <integration>/Blade.fsproj -c Release` passed with 4 NU1900 package-audit warnings and 0 errors. The combined `Blade.exe test optimize` passed 608, failed 0. Full stdout/stderr and provenance, with private TEMP/TMP, are in `C03-runner/release-build.*` and `C03-runner/test-optimize.*`.

The exact six-value representative source was saved as `C03-runner/two-folds.blade` (SHA-256 `25F362108B527EA18CA9E02A4E4CF973866A132009DFF0CB58C16E2BB4D2A3B3`). Environment pins were result-only `BLADE_PRINT=result`, `BLADE_FP_REPRO=1`, `BLADE_FP_CONTRACT=off`, `BLADE_BLAS=off`. No program was evaluated.

## Early topology observation

The final probe used the public parse/typecheck pipeline and public `Lowering.lowerTypedModule`, then reproduced the relevant single-module prefix: `IRMono.monomorphizeModule`, whole-program HM and shape specialization, typed-zero/float/fold-seed adaptation, `refuseEnumKeyArithModule`, and `lowerArrayBinOpsModule`. It captured the checked module immediately before `Optimize.fuseSelectedTerminalModule`, the boundary at `Lowering.fs` 3116–3123. It did not inspect `lowerCaptured` output, which is after selected and ordinary optimization. The source has no imports, effects, writes, providers or foreign module references; `foreignRefs` is empty. Private preflight-only safety checks are not exposed by `Lowering`; they were not duplicated in the probe and are unnecessary to this pure input's shape. The probe does not call the selected pass or `optimizeModule`.

The result matches the proposed exact host topology, with IDs and types observed:

- Executable binding order: `x` at position 0/id 1 (`Array<Float64 like Idx<6>>`), `keep` at 1/id 3 (`Array<Bool like Idx<6>>`), `y` at 2/id 5 (`Array<Float64 like Idx<?>>`), `result` at 3/id 8 (`(Float64, Float64)`). Thus `keep`, `y` and the tuple host are adjacent in executable binding order.
- `keep` is `IRMask(IRVar id=1, predicate IRVar id=9)`. `y` has an `IRCompoundInits` entry keyed by id 5 with dense input `IRVar id=1` of `Array<Float64 like Idx<6>>` and mask input `IRVar id=3` of `Array<Bool like Idx<6>>`.
- `result.Value` is directly `IRTuple [IRReduce(IRVar id=5, kernel IRVar id=12, seed 7.0); IRReduce(IRVar id=5, kernel IRVar id=13, seed -2.0)]`; both source occurrences are the same y id. Both kernels have `IRArrow([Float64; Float64], Float64)` types; the host type is exactly `(Float64, Float64)` and its component types are `[Float64; Float64]`.

Full reflection-backed IR and provenance are in `C03-runner/topology.stdout.txt`, `topology.stderr.txt`, and `topology.provenance.txt`. The successful probe source is `preselected-topology.fsx` (SHA-256 `EB01C80D1761E83E5EBA851B713CD055540452A25004A08A2B5A55CA081C099D`). Early probe attempts with F# type/name mistakes are retained as v1–v4 scripts/logs; they failed before probing and are not compiler or test failures. The graft query had no indexed F# entries under `src/Lowering.fs`; the stage was verified by targeted reads of the public entry and its documented call site.

## Existing compiler emission and plan baseline

Both CLI emissions and plans used the same source, compiler identity and environment pins; they generated source/IR only. Neither C++ nor LLVM was compiled or run. The first C++ `emit` invocation wrote generated C++ to stdout; that complete stdout was retained and reused as `baseline-cpp/emitted.cpp` rather than rerunning the successful emission. `plan --json` then passed and parsed as JSON. The direct LLVM `emit` wrote LLVM IR to stdout, identified by the generated-by-LLVM header; its plan also parsed as JSON.

Both plans report `selected-terminal` declined for `y` because the current rule requires exactly one direct seeded scalar terminal. They also report `sibling-folds` declined because reductions over compound `y` do not use one plain dense literal-extent axis. `compaction-fusion` and `compound` are applied: the stored mask count is computed in its mask pass, then a single branchless fill/gather materializes compact `y`; positions are elided. Under the pinned reproducible serial policy, both tuple-leg fold forms remain serial.

The C++ output contains the `keep` count loop, `y_compact` storage, one fill/gather loop, and two separate fold loops over y inside the tuple's `make_tuple` at emitted-source line 232. The LLVM output has exactly 3 `@blade_alloc_cells` calls: input x (6×8 bytes), keep (6×1 byte), and compact y (runtime cardinality×8 bytes); its two fold loops are separate. These are emitted-source observations, not runtime allocation or performance measurements.

Artifacts and SHA-256:

| Artifact | SHA-256 |
|---|---|
| C++ emission `baseline-cpp/emitted.cpp` | `AF569E2BBAC20A7EC67E903641FD7771C490722746FDEC2A94B1E03471DD151C` |
| C++ plan `baseline-cpp/plan.json` | `141E2036ED1B7A4BEBA7B0E72878216579C89BA60168CCBA6380562D50B47693` |
| LLVM emission `baseline-llvm/emitted.ll` | `287B56E4584E8B81EF0EE38D12CAEE84ECF98BDF5651FB1462873858EB5ACC61` |
| LLVM plan `baseline-llvm/plan.json` | `D3A2F37752EF8D593B894F5089FB414DC468354979B9A62768B88F4744AC8566` |

The expected `(13, -1868)` is the independent hand calculation from selected values `[1, 3, 2]`; this diagnostic did not execute the program. No allocation-saving, traversal-sharing, predicate-sharing, timing, peak-memory, or runtime claim is made. This evidence supports root review of the narrow proposed topology; implementation remains unapproved and pending root dispatch.
