# Codex Adversarial Review

Target: working tree diff
Verdict: needs-attention

Do not ship yet: the new outer-kernel admission can erase a named `where repro` boundary, and threaded partials ignore BLADE_FOLD_LANES=4. The ordinary scheduling paths checked out in read-only `blade plan` runs; runtime tests were not run. BL4005 was confirmed at mutability-errors/042:15:23. No files were edited.

Verdict table for the 19 objections (partial means an incomplete proof or coverage, not a demonstrated miscompile):

| # | Verdict | Evidence |
|---|---|---|
| 1 | Closed | Buffer bound locally for write analysis: src/Optimize.fs:3603-3608. |
| 2 | Closed | Both history lower bounds enforced: src/Optimize.fs:3568-3572. |
| 3 | Partial | Fresh initializer checked at src/Optimize.fs:3591-3597; independent seed/extent proof remains absent for trajectory-only entries. Normal desugaring supplies it: src/TypeCheckInfer.fs:14116-14138. |
| 4 | Closed for inspected shapes | Existing map/kernel restrictions retained: src/Optimize.fs:3313-3330,3420-3463; extra time-body statements fail emission at src/CodeGenBinding.fs:6145-6155. |
| 5 | Closed for tracked aliases | Transitive alias set and direct-row restriction: src/Optimize.fs:3515-3552. |
| 6 | Closed | Root eligibility follows returned bodies; operands and initializers receive false: src/Optimize.fs:3392-3458. |
| 7 | Partial | src/CodeGenBinding.fs:6137-6143 checks top-level result types, not a recursive scalar-expression whitelist. |
| 8 | Open | Step callees decline repro at src/Optimize.fs:3602, but newly admitted outer repro kernels lose their boundary; finding 1. |
| 9 | Closed for current parallel forms | Parallel K restricted to exact Square; arbitrary K and F remain serial: src/CodeGenBinding.fs:6180-6183,6276-6309. |
| 10 | Closed | Parallel row pass requires OuterRequest: src/CodeGenBinding.fs:6180-6183. |
| 11 | Partial | Float/double and lanes-off guards exist at src/CodeGenBinding.fs:6171-6175; four-lane selection ignored at :6233. |
| 12 | Closed subject to repro finding | Square-only partials; ascending actual-team combine; original F statements retained: src/CodeGenBinding.fs:6171-6175,6264-6275. |
| 13 | Implemented; edge coverage incomplete | Zero-width clamp, actual-team partition/publication, and deletes: src/CodeGenBinding.fs:6238-6254,6272-6275,6299. |
| 14 | B2r fallback closed | CellFoldTable still recorded at src/Optimize.fs:3977-3980; suppressed only while chunk bounds are active at src/CodeGenBinding.fs:5991-5997. Exact old/new emission identity was not established. |
| 15 | Partial | Final shape check does not revalidate history indices, StepLo, fold expression, or accumulator target: src/CodeGenBinding.fs:6126-6153. No current-pass miscompile demonstrated. |
| 16 | Partial | Reset and chunk cleanup exist: src/Lowering.fs:2880; src/CodeGenBinding.fs:6227-6230. Mutable entries remain ambient rather than program-bound: src/Optimize.fs:2362-2382; repeated/interleaved emission is untested. |
| 17 | C++ closed; LLVM partial | C++ end census records through ompSuppressedPhrase: src/CodeGen.fs:3912-3918, src/CodeGenState.fs:951-955. LLVM hook records only with active decisions; textual census still scans callables: src/EmitLlvm.fs:3000-3006,7988-7993. |
| 18 | Partial | Persistent-team observation is real, but strict post-pass team execution is not independently observed: tests/RecStepTests.fs:138-139,277-281. |
| 19 | Partial | Separate team/hash/scalar checks are present at tests/RecStepTests.fs:275-289. Gate-off needles at tests/OptimizeTests.fs:3139-3141 can match an unused step definition; the post-pass pragma needle at tests/RecStepTests.fs:95 can match PARALLEL_FOR_SIMD elsewhere. Repro-boundary, stale-entry, empty-work and nonassociative-seed cases remain uncovered. |

Findings:
- [high] 1. Preserve the named repro boundary when admitting the outer fold (src/Optimize.fs:3901-3905)
  outerKernelOk newly admits an omp kernel even when IsRepro is true. foldUpdate then substitutes f.Body directly into the accumulator assignment (src/Optimize.fs:3808-3814). StrictOnly merely disables licensedSquare; the post-pass renders that arithmetic in the caller without the original BLADE_REPRO_FN boundary (src/CodeGenBinding.fs:6284-6286,6303-6305). A named Float64 fold such as `where comm(a, b), omp, repro` with body `a * b + (a + b)` can therefore contract in a default contracting caller, violating the explicit repro contract. Previously the omp flag prevented this row-fold fusion. This is a source-traced numerical failure path, not an executed reproduction.
  Recommendation: Preserve calls to repro kernels instead of splicing their bodies, or decline this fusion for them. Add a contraction-sensitive named outer-kernel test with global repro unset and contraction fast, compared against fusion disabled.
- [medium] 2. Honor the configured lane count in threaded self-dot partials (src/CodeGenBinding.fs:6231-6233)
  The admission checks only whether fold lanes are off, then emits `blade_fold::sq<elem, 8>` unconditionally. BLADE_FOLD_LANES=4 consequently produces four-lane ordinary self-dots but eight-lane threaded partials. This silently ignores the requested reduction tree and invalidates lane-specific numerical and performance comparisons. The existing prodsum emitter obtains the configured count through foldVecLanes (src/CodeGenExpr.fs:486; src/CodeGenExprSupport.fs:2387-2390). Current rec-step tests exercise default and off settings, not four lanes.
  Recommendation: Use foldVecLanes VecSq when generating the partial call. Add a BLADE_FOLD_LANES=4 assertion scoped to the persistent region and validate against the four-lane partial model.

Next steps:
- Fix both findings and run rec-step, optimize, recursive-arrays, interpreter and opt-diff gates with skip totals checked.
- Exercise stale-entry rejection by changing bounds, history reads and fold/update expressions while retaining IDs; strengthen validation wherever a region still emits.
- Scope emission assertions to the relevant function and region, independently instrument the strict post-pass team, and add repeated-emission, empty-work, nonzero-seed and nonassociative-F cases.

Codex session ID: 01a15b3a-7a5e-74f2-92f0-b864780f3da5
Resume in Codex: codex resume 01a15b3a-7a5e-74f2-92f0-b864780f3da5
