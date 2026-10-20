# Loose ends: second opinion

## Priorities before merging this round

1. **Fix the serial input-generation regression (item 1):** commit `b08a9aef` globally substitutes the split Int64-to-Float64 conversion. Scratch A/B restores the old speed by changing only that conversion back. Preserve the band's vectorization improvement with a range/context-aware lowering, rather than reverting the whole branch.
2. **Fix harness evidence and reporting (items 8-9):** recognize scalar K-lane trees, preserve strict-repro rejection checks, and exclude the broken timing-script output. Correct the repro-contract claims, omitted losses and default-K wording.
3. **Keep measured defaults stable (item 7):** retain anti_kc=0 and K=8. Treat the unmerged BLAS preference change as a separate policy addition; no MKL speed claim is supported by this round.

Can wait: broader fold fusion, compact checksum scheduling, join threading and recursive spatial-map threading (items 2-6), with focused correctness tests before later performance claims.

Status: **all nine items complete**. Saved incrementally after each item; final audit completed 2026-10-19. Only this new report was written in the repository; all diagnostic artifacts are under the permitted TEMP directory.

## Scope and evidence

Read `CLAUDE.md`, the brief, the Round 3 record and the write-ups. No tracked files edited, no staging/commits/branch changes/stash, no `blade test`, no harness benchmark sessions. The runner-window flag was absent before diagnostic invocations; it is checked again before each compilation/run. Scratch is `%TEMP%/codex-loose-ends/`. Graft's skill was read, but neither its executable nor `graft/INDEX.md` exists here; direct source inspection is the fallback. NEW/OLD Blade.dll SHA-256 values were checked and match the brief: BB52A4C2A008D1985C4DF89A69A3ED54327BB25F1301BDE0FECFA2ADC5C3BF45 / F9BA95CDAAE63EFED006465602FC8B7A2E891AD11A79DC02E86C18BCDD14F5CD. Timings below are short diagnostic A/Bs without protocol affinity/drift controls, not new protocol verdicts. Ratios in the published record remain expert/Blade.

## 1. Serial C++ input-only regression: isolated to band conversion

**Cause, confirmed:** `b08a9aef` (band G30/G33/G31), specifically the unconditional replacement of `static_cast<double>(Int64)` by `blade_arith::i64_to_f64`, not fold-simd's `4e49f46a` product barrier. The helper (`src/cpp/blade_runtime.hpp:214-224`) expands to a high/low-word split on this AVX2 host. That helps loops which can exploit packed conversions, but costs more in this integer-generator loop.

I emitted the unchanged full-size `dot/inputonly.blade` (3,000,017) and `saxpy/inputonly.blade` (2,000,003) through OLD, NEW and all seven branch binaries, with BLAS off, OMP emission suppressed and fast contraction. For BOTH programs the entire emitted files fall into exactly two byte-identical groups:

- OLD = g32-g34 = maskagg-compact = tri-tiles = fold-simd.
- NEW = band-vec = blas-repack = recur-dest (the last two contain the band change).

The complete old/new source diff is only: remove `BLADE_FRAME` in `gen` and its forwarding lambda; replace `static_cast<double>(__v10)` with `blade_arith::i64_to_f64(__v10)`. The input checksum folds are unchanged expression-position K-lanes(8) sums, with no product and no `blade_fold` helper invocation. Thus the product-rounding barrier cannot explain this twin. Git's diff for `b08a9aef` also identifies both the cast replacement and frame scan change.

For isolation, I compiled the emitted programs with the current runtime headers and `g++ -std=c++17 -O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv -fopenmp`. A fifth scratch variant changes ONLY NEW's `gen` conversion back to `static_cast<double>` and leaves its frame removals intact. Five interleaved single runs per variant, alternating the order each round, measured the program's internal timer:

| Variant | dot median ms | saxpy median ms |
|---|---:|---:|
| OLD emission | 18.031 | 12.220 |
| NEW emission | 22.960 | 15.620 |
| band-vec emission | 23.149 | 15.839 |
| fold-simd emission | 17.714 | 12.030 |
| NEW, only conversion restored | 17.989 | 12.164 |

NEW/OLD is +27.3% / +27.8%, closely reproducing G37. Restoring the conversion recovers the loss while keeping the frame improvement. All printed input checksums matched within each kernel. Raw samples: `%TEMP%/codex-loose-ends/twin-diagnostics.csv`; emit files, compiler stderr and run output are beside it. These timings use common runtime headers deliberately to isolate emission, not separately installed branch runtimes.

**Fix:** avoid globally imposing the split on scalar conversions. A proven signed-32-bit range can use an exact int32 conversion; otherwise preserve the direct scalar cast and introduce the split only where packed vectorization benefits. The generator's final signed remainder is in the signed-32-bit range, so this case admits narrowing without guessing input positivity. Preserve band's own conversion/vectorization and panic-frame fixes. Cover negative remainders, full Int64 extremes and repro raw bits; retime dot/saxpy twins and band in the runner's next authorized window. The LLVM and mt8 timing flatness does not exonerate the conversion: backend/vectorization and parallel memory costs differ. G38 should first be retested after this fix, since maskagg uses the same generator.

**Recommendation:** fix the conversion selection before merging; **medium, this round**.

## 2. Pairkernel mt8: no remaining large serial orbit pass

The premise needs correcting: the final mt8-licensed emission already threads the expensive producer AND the orbit checksum. In session `135344`, `pairkernel.cpp:290-301` uses `BLADE_OMP_PARALLEL_FOR_DYNAMIC` for the packed triangular producer; lines 313-340 open the checksum team, with a SIMD off-diagonal sum in each assigned row. Its plan explicitly records both. The serial work is allocation/skeleton construction, tiny thread-partial combines (two classes times eight threads), and orchestration; there is no remaining full-pool serial checksum to parallelize in this regime. `allocate` uses uninitialized `new T[]`, not a serial zero-fill of the 25 MB pool (`nested_array_utilities.hpp:418-425`).

I inserted phase timestamps into a scratch copy of that archived C++ and built it with the same flags as item 1, `OMP_NUM_THREADS=8`, `OMP_DYNAMIC=FALSE`. Five single runs gave median phase times: input/allocation/team startup **0.896 ms**, output allocation **0.029 ms**, triangular kernel **5.425 ms**, orbit checksum **1.463 ms**, remaining input checksum/combine **0.064 ms** (component medians need not sum to median total; total median 7.841 ms). Instrumentation is diagnostic and has no drift controls, but it identifies where the time goes. Files: `%TEMP%/codex-loose-ends/pairkernel-phases*`. PowerShell labels the program's stderr timing lines `NativeCommandError`; these are stderr formatting records, not program failures (the programs printed valid results and completed).

`expert.f90:56-80` threads descending columns dynamically and immediately rereads each completed column for its checksum inside the SAME team. Its SoA x/y/z vectors also avoid Blade's point-row pointer loads; it stores diagonal 1 directly. Blade finishes the full producer, closes the team, then traverses the pool in a different boustrophedon assignment under a new team. Thus the main actionable gap is **producer-local checksum scheduling/locality and team reuse**, not an absent thread pragma. The expert also avoids opening teams for the tiny input and input checksum. Most time is still the Matern evaluation; the phase timing alone cannot assign all of the ~1.5 ms expert advantage to any one of these differences.

Extend the compact producer scheduling to accumulate per-row orbit classes while the row is hot, with class partials combined in the existing licensed manner. Retain the matrix if demanded by the benchmark output contract. Consider a small-work threshold for opening input teams. AoS/SoA and libm costs deserve separate profiles rather than being bundled into a claimed fix. G32 should remain open as a performance outcome, while its old 'serial orbit pass' explanation should be retired for the final tree.

**Recommendation:** correct G32's causal wording now; prototype compact producer/checksum scheduling and small-team thresholds separately; **medium, later** (wording: **small, this round**).

## 3. Mt8 repro: parallelize independent cells, never partition one ordered chain

**Soundness:** a fixed partition is not sufficient. `(a+b)+c` and `a+(b+c)` can differ even if the partition never changes. Repro requires the interpreter's exact expression/seed/order for each fold, not just deterministic results for a chosen thread count. Workers may compute independent cell/row results into indexed slots; the parent must then combine those slots in the original ascending order, with the original seed and contraction policy. Arbitrarily assigning contiguous chunks of one scalar reduction to workers and combining partials is NOT legal under `BLADE_FP_REPRO=1`.

The Fortran premise also needs qualification. The experts preserve certain *inner* chains, but their mt8 outer `reduction(+:...)` still changes global association. In `band/expert.f90`, even the non-BENCH_LICENSED arm has `parallel do ... reduction(+:s)` over rows: that is a partition of one global sum, not Blade's exact repro contract. Dot/saxpy/fusedchain likewise use global OpenMP reductions in their repro arms. Comoment3 and pairkernel additionally use multiplicity-weighted packed checksums in repro, unlike Blade's ordered logical walk. These comparisons measure different reproducibility constraints; they are not evidence that Blade can simply enable the same reductions without changing its contract.

| Kernel/work | Strictly legal independent work | Chain that must keep its association |
|---|---|---|
| comoment3 tensor | Each tensor cell's ascending observation prodsum; already covered by the threaded simplex tile | The logical checksum has nested k, j, i folds. Compute a complete j-row or i-slab independently, preserving its nested inner chains, then fold indexed results in ascending parent order. Do not replace it with weighted canonical cells in repro. |
| pairkernel matrix | Each Matern cell, already threaded; each full logical checksum row can be computed independently | Each row's ascending j sum and the final ascending i sum. A parallel row-result array + serial outer combine preserves both. |
| band | Independent term evaluation into a bounded tile/buffer | Source is ONE reduce over the flattened lexicographic constrained domain. Row partial sums add parentheses absent from the source and are not legal. A parallel term producer followed by a single serial term fold is legal but may add enough traffic to lose. |
| dot | Product evaluation | ONE global product sum. Partial scalar sums cannot preserve its bits; the two input sums are two further global chains. |
| saxpy / fusedchain | Independent output cells or mapped terms | ONE checksum chain; each input sum is also global. Producer/checksum loop fusion can retain the chain serially; thread partial reductions cannot. |
| stencil | Independent Laplacian rows, each row's ordered square sum; independent input-row sums | Ascending outer combine of completed rows. The kernel hot-row path ALREADY does this in final mt8 repro; `session-145930/.../stencil/.../plan.txt` explicitly says the row folds run inside the producer's team. The input row-fold pass is a remaining candidate. |

For pairkernel/comoment3, add an emitter path recognizing a pure map-of-fold under an ordered outer fold: allocate one result slot per outer element, partition those independent computations across the licensed team, preserve the inner fold implementations (including first-element versus explicit-zero initialization), join, then run the unchanged outer fold. Start from the existing hot-row-fold model rather than changing Path B's `fpReassocEnabled` guard (`CodeGenBinding.fs`, around 4037 onward). Repro and `where repro` must continue to veto regrouping; diagnostics should separately report parallel producer evaluation and ordered combine. Preserve per-cell effects/alias checks and decline effectful producers. LLVM currently has no thread runtime, so this is a C++ path first, with interpreter raw-bit comparisons.

This is a **medium** targeted implementation for nested logical checksum rows, with cancellation-sensitive, signed-zero, odd-size, tiny-size and 1/2/8-thread tests. A general scheduler for arbitrary nested producers/joins is **large**. It can reduce G35 for pairkernel/comoment3; it cannot promise to eliminate the band/dot/saxpy global-chain losses. No implementation or tests were run here.

**Recommendation:** keep the strict contract; add indexed parallel row evaluation plus an unchanged serial parent fold, and correct the Fortran-equivalence claim now; **medium, later** (documentation: **small, this round**).

## 4. ifx fusion: verified, with regime-specific forms

I inspected the actual Round 3 ifx executables with `objdump`, not just their source comments. Scratch disassemblies are `ifx-<kernel>-<regime>.asm` under `%TEMP%/codex-loose-ends/`. The expert sources spell separate input generation, kernel and input-sum loops, so the following fusion is compiler-generated.

- **Dot repro (144145):** `0x140001af0..0x140001bce` is one loop loading a/b, updating the scalar product chain with `vfmadd231sd`, and updating both input sums with `vaddsd`. The input generator remains an earlier pass. This directly verifies kernel/input-checksum fusion.
- **Onepass repro (144145):** `0x1400019e0` onward updates the four power sums and a fifth input-sum accumulator from the same loaded values. It retains two equivalent x-sum chains rather than proving them identical and eliminating one.
- **Saxpy repro (144145):** `0x140001b90..0x140001c32` computes/stores y2 and accumulates its square checksum alongside input x/y sums. Its two independent input chains are packed into an XMM register; that does not itself reassociate either chain.
- **Fusedchain repro (144145):** more accurately **64-element block scheduling**, not one scalar fused loop. The outer loop advances by `0x200` bytes; within each block it sums input values (`0x140001ac0`), vector-computes mapped terms into a stack buffer (`0x140001b30`), then consumes those terms in an ordered scalar chain (`0x140001bd0`). This improves locality while preserving each sum's order.
- **Fastmath dot/onepass/fusedchain (153107):** stronger fusion includes input generation itself. The loops around dot `0x1400019f0..0x140001ad6`, onepass `0x140001970..0x140001a14`, and fusedchain `0x140001980..0x140001a2b` contain integer generator instructions, input-array stores, kernel arithmetic, and input-sum vector accumulators together. Input stores survive. This is an additional reason fastmath moves more than Blade, beyond simply fusing two read-only folds. There is no Round 3 fastmath saxpy cell; do not imply that it was measured.

The recorded ifx repro command is `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp-simd`; fastmath uses `/fp:fast`. The wrapper `bench/fortran/build_ifx.cmd` passes those flags through. Hence the suite's repro regime still permits contraction; it is not the stronger interpreter differential mode with contraction off. The observed repro FMAs are consistent with its flags.

**Legality for Blade:** fusion of pure independent traversals over the same index space is legal in all three regimes if it retains every chain's original order, seed, intermediate rounding requirements, output materialization and observable effects. Repro can fuse dot plus two input sums; saxpy can write its output and update its checksum/input chains; onepass can jointly traverse its four chains and the input sum (or share a sum only after proving identical seed/order); fusedchain can use bounded term buffering with an ordered consume. Contraction-off still forbids introducing an FMA across a required rounded product. Licensed folds can use their existing licensed trees, but the licence for one fold must not silently license another or arbitrary algebraic changes to mapped values. Blade cannot copy `/fp:fast` algebraic rewrites wholesale on the strength of a fold licence.

Start with sibling-fold fusion after pure call inlining and explicit dependence/effect checks, preserving the separate logical chains. Extending it across fresh input generation is a larger scheduling change; retain required stores under the benchmark's compiler-fusion allowance. Assembly verifies the mechanism, but does not quantify what fraction of each timing gap it causes. Total-minus-twin is especially not an isolated kernel timer when the full program fuses work differently.

**Recommendation:** record these actual per-regime forms and implement general pure sibling-fold fusion, with strict order/rounding pins; **medium, later** (generator fusion: **large, later**).

## 5. Maskagg: general compound representation still pays several extra passes

The rank-1 specialization fixed the hash-table disaster, but it does not yet emit the expert's simple compaction pipeline. Both archived plans (`133604/.../maskagg/.../plan.txt`) say **count pass + branchless kept-position fill + gather by position**. LLVM is a real LLVM arm now, not fallback.

The C++ at `generated/maskagg.cpp:226-261` and LLVM `generated/maskagg.ll:125-236` retain these stages after generating x:

1. Materialize one boolean per input element for `keep`.
2. Count that boolean array to size the selected-position buffer.
3. Scan it again, branchlessly filling selected Int64 positions (one spare slot handles rejected trailing elements safely).
4. Allocate y and gather x at those positions.
5. Allocate and materialize a second compact value array for `y*y`.
6. Fold that square array, then separately fold x for the input checksum.

For a half-selected million-element input this includes roughly 1 MB of mask plus 4 MB each of selected positions, y and squared-y, in addition to x. `index_types.h:389-402` confirms the rank-1 constructor is already branchless. Calling this remaining gap simply 'branchy compaction' would be wrong.

The expert (`expert.f90:42-55`) allocates y to capacity n, writes each candidate to `y(m+1)` and increments m by its predicate, then folds only `y(1:m)` directly. It needs no materialized mask, count pass, index-position pass, gather, or squared-output array. The generic representation retains keyed lookup capabilities that this use site never observes.

The session supports a real compaction/consumer gap independent of G37: LLVM total **4.680 ms**, twin **1.643 ms**, incremental **3.036 ms**; ifx expert total **2.939 ms**, twin **1.751 ms**, incremental **1.188 ms**. Thus the LLVM input is already a little faster, while the remaining pipeline costs about 1.85 ms more. C++ has BOTH this gap and the item-1 generator regression (total 7.054, twin 3.774, incremental 3.280 ms). These are indicative cells, and incremental numbers are differences of medians, not separately timed phases.

First extend map/fold fusion across compact arrays to avoid `y*y`, preserving the source fold's first-element seed, empty-array BL8003 behavior and required rounded products (especially with contraction enabled). Then add an escape/use-aware mask+compound path: if neither mask nor keyed index mapping is observed, compact values directly into capacity-n storage and keep the resulting cardinality; retain y as the output. If keyed access is needed, preserve/build the position map. An intermediate improvement can fill positions and values together after counting. Both lanes need the same optimization policy; changing the public compound semantics or replacing an empty reduce with zero would be incorrect.

**Recommendation:** keep the landed representation fix, then remove the square temporary and specialize unobserved mask/index metadata; **medium, later**. Retest G38 after the item-1 fix **this round**.

## 6. Why onepass and recur have no Blade mt8 arm

**Onepass is an emitter/API gap, reflected by the source's missing thread licence, not an inherently sequential algorithm.** The input map has `where omp`, but the four-leg `<&!>` fold uses builtin `(+)` with no thread request. Builtin addition admits the licensed SIMD form (the archived plan records a four-leaf SIMD fold); a reorder licence is not itself a request for a thread team. `CodeGenBinding.fs:4762-4782` explicitly states that a join opens no team and emits a decline if a leg does request `omp`. Merely adding annotations or deleting `no_parallel_form` from kernel.json does not implement it.

A licensed join path can reuse one partition across the shared traversal, maintain a tuple of per-worker accumulators, apply each leg's legal vector form, then combine each leg's partials in a specified order. Validate every leg's licence and purity; a mixed join containing an ordered/noncommutative leg cannot silently inherit the other legs' permissions. In repro, these are four separate GLOBAL chains, so the usual per-worker partial combine is not legal. Sharing their traversal is already legal and implemented; adding a fifth input-checksum chain is item 4, not eight-thread reduction.

**Recur is sequential in t, but this particular ODE is independent in x.** The source's `rhs` and `step` are pointwise, so each spatial cell can evolve through all 301 steps independently while storing the trajectory. `expert.f90:52-96` opens a persistent team and gives each worker its x interval for every t; it does not parallelize dependent time steps. Blade's source has no spatial-thread licence slot in its implicit array-valued recursive step, and the landed destination-write/row-fold fusion does not add one.

A safe extension needs an explicit/inherited spatial-map licence carried through recursive slice lowering, dependence checks, and a persistent team or proven independent per-x trajectories. For general recurrences with neighbor reads, retain per-step barriers; this benchmark's pointwise dependence can avoid them. Preserve the output trajectory and strict nested checksum association: the expert's thread-local checksum partials are not a model for Blade's repro checksum. Do not claim all recursive arrays are parallelizable, or all are necessarily serial in their spatial dimensions.

The `no-parallel-form` cells are honest missing features. The source currently lacks licences BECAUSE the useful corresponding paths are absent; it is misleading to attribute the whole gap solely to benchmark authors declining to annotate an available form.

**Recommendation:** keep the no-parallel-form labels; add licensed tuple-fold threading (**medium, later**) and a proven spatial-recursion threading path (**large, later**).

## 7. Small decisions

**`anti_kc`: keep 0 for this round.** The printed A/Bs in RESULTS 14.5 do show a directional improvement for 200: LLVM 27.6/27.5 -> 26.8/26.7 ms (~3%), C++ 30.4/30.5 -> 29.2/29.1 ms (~4%). The prose 'within noise to ~2%' understates those displayed point differences; the correct statement is that this short A/B has not established a robust general default. Only one principal shape and limited rounds were measured; the 128 second pass was disturbed. Keep 200 as an explicit tuning candidate, and compare more non-power-of-two sample lengths, including around KC boundaries, serial/mt8 and repro before changing a portable default. **Recommendation: retain 0; small, this round; wider tuning later.**

**`BLADE_FOLD_LANES`: keep default K=8.** Source resolves the ambiguity in RESULTS 14.6: `foldLanesMode` maps unset to Auto and `foldVecLanes` returns 8 unless an explicit 4/8 overrides it (`CodeGenExprSupport.fs:1999-2009,2045-2048`). Default and explicit 8 are the same algorithm; their timing differences are measurement variation, not another policy arm. Whole-program neutrality does not erase the useful cache-resident gains, but the microbench ranges do not prove K8 universally beats K4 (some reported K4 rows are higher). The item-1 regression is unrelated. Keep the escape hatch and correctness-tested form; describe G25 as a delivered threading+SIMD capability and G26 as a microkernel gain, not a demonstrated whole-suite speedup. **Recommendation: retain K8 and correct the default/benefit wording; small, this round.**

**`BLADE_BLAS_PREFER` (`5d1a3814`): useful explicit control; no new correctness blocker found in the inspected patch.** I read the routing changes for both gram(A,A) and same-array row contractions, the toolchain/setup integration, and the added dispatch tests. It reads per call, honors env over file, leaves the BLAS gate authoritative, retains the separate tile-off switch, and records the selected route and setting origin. The gram-node path only preempts HostBlas, preserving an explicit device route. The new tests cover both syntaxes, gate on/off, aliases, default/auto/MKL/native/library, tile-off, source reporting and env/file precedence; the brief's green result is recorded in RESULTS 14.3, not independently rerun here.

Two decisions should be explicit before including it: (1) `auto -> library` for MKL is a vendor heuristic with NO MKL performance measurement in this round; I would prefer a measured native default plus explicit `library` until that evidence exists, or retain the heuristic only with its current clear disclosure. Do not call it a measured fastest-choice rule. (2) The measured tree has no such knob, so its existing results must remain attributed to d3d5722b/default native-preempt. A later binary needs its own identity and route evidence; do not relabel round-3 data as a test of the knob.

Before a merge containing the branch, ask the authorized runner's normal integration process to cover preference-state restoration, runtime/small/complex/f32 ineligible shapes, explicit cuBLAS precedence, and the D47 harness accept/reject cases. The current D47 parser matches structured same-subject decisions, so the added preference text should not itself break it. The harness's library import detection is OpenBLAS-specific (`run.py:1128`), so this benchmark harness is not already an MKL benchmark just because the compiler recognizes MKL. No need to block the measured fixes on this optional policy feature.

**Recommendation:** keep both tuning defaults; land the BLAS control separately after resolving/documenting the unmeasured MKL default and integration coverage; **small, later** (or a clearly separate addition this round).

## 8. Write-up audit against the Round 3 record

**The main numeric transcription is good.** I programmatically compared all **62** kernel/regime rows in RESULTS 14.8's final tables with RUNNER-RECORD Round 3: Blade medians, expert medians, both ratios and confidence intervals, and live/indicative labels for cells with a Blade reference match. No changed number or swapped live/indicative label was found in those fields. Missing-arm rows are intentionally labeled 'no Blade arm', not a performance verdict. The record has 40 suppressed blocks total, of which 37 have a Blade reference; three suppressed blocks are missing-arm rows. Thus '40 of 62' is correct and should not be rewritten as 40 measured Blade comparisons. Spot-checks of G32 and G35-G39 against the record also reproduce their quoted ratios and stars.

Corrections/qualifications needed in the prose and register:

1. **G37/G38, RESULTS 14.6/14.8:** 'undiagnosed' is now superseded by item 1. Attribute the demonstrated twin regression to `b08a9aef`'s unconditional split conversion, not to fold-simd. Preserve published measurements; add the new causal evidence separately.
2. **G35 and RESULTS 14.8:** the proposed 'fixed per-thread partition' needs the legality boundary from item 3. It works for independently evaluated complete inner folds followed by the original outer chain, not for chunking a global reduction. The expert's repro OpenMP reductions are not byte-identity equivalents. Stencil's kernel row folds are already threaded inside the hot-row producer; treating its entire checksum pipeline as unthreaded misses the final plan.
3. **G36 and RESULTS 14.8:** fusion is now assembly-confirmed for the named repro arms, with blocked scheduling for fusedchain. Fastmath includes generator fusion for the three inspected kernels. Also state that Blade's licensed and fastmath configurations are intentionally identical (`bench/harness/config.json`, `blade_knobs.fastmath`); 'Blade barely moves under fastmath' is expected policy, not evidence of a failed optimization switch.
4. **RESULTS 14.5 / G1 remnants:** 'anti_kc ... within noise to ~2%' and the categorical 'no measured gain' should become 'small directional improvement, insufficient evidence to change the default'. The displayed 200-vs-0 medians imply about 3% LLVM and 4% C++, as item 7 details. No statistical significance is established by those point values.
5. **RESULTS 14.6:** 'the record does not say which K the shipped default resolves to' is avoidable ambiguity: source says **8**. Default/8 are duplicate algorithm arms. The record's microbench evidence should not be promoted to a whole-kernel speedup; its current 'wash' conclusion is appropriate.
6. **'Kernel-only' wording** in 14.4/14.6/14.8 and G36 should read **incremental (total minus input-only twin)**. The subtraction is correct, but fusion changes the full program's scheduling, so it does not isolate the kernel execution time. The ifx repro dot 0.59 ms figure is an incremental difference, not a direct kernel-phase observation.
7. **'The serial loss is maskagg'** is safe only as 'the remaining serial-licensed loss to both experts'. Saxpy's ifx loss in the same regime is already admitted nearby, and repro has more losses. Likewise the group-B input-twin diagnosis should name the programs checked rather than imply every compiler-lane deficit is solely input cost.

8. **RESULTS 14.6 numerical overstatement:** the corrected `p2r3/fold-ab_time-fixed.txt` does not support `ab_time ... within 1-3% everywhere`. Its off/K8 ratios include **0.952 for mt8 stencil** and **1.057 for mt8 onepass**, and off/K4 is **1.038 for mt8 fusedchain**. These are process-wall timings from a small diagnostic run, so avoid turning those deviations into performance verdicts either. Use the exact range or say broadly neutral/no robust whole-program gain; keep the raw corrected output identified.

**Losses omitted from the dedicated 'losses that remain' bullet list:** maskagg itself is only in the preceding headline. Add BOTH serial-licensed **0.871*/0.628*** and serial-repro **0.839*/0.676*** (the repro maskagg loss is absent from that headline as well). Serial-repro band's gfortran loss **0.844*** is mentioned in the headline but omitted from the loss bullets; include it there for completeness. All are present in the final tables, so this is a summary omission, not missing data. Ratios near one whose intervals straddle one (e.g. mt8 licensed dot/band) should remain parity/indicative, not be inflated into another confirmed loss merely because their point ratio is below one.

The register's dated Oct-18 opening snapshot is historical and clearly followed by a Round-3 update; it is not a numeric inconsistency, but putting the latest snapshot first would prevent readers mistaking G28/G30/G31/G34 for still-open implementation gaps. Preserve the distinction between G34's fixed LLVM excess and the residual common-lane repro loss.

**Recommendation:** make the causal, contract and summary corrections above; retain the correctly transcribed Round-3 tables and all drift qualifiers; **small, this round**.

## 9. Remaining loose ends, harness issues and focused tests

**Harness fold evidence has a concrete blind spot.** `bench/harness/run.py:882` recognizes `BLADE_OMP_*SIMD_REDUCTION` and `blade_fold::{sum,dot,sq,dot3}<...>`, but not the scalar `__rlane0..7` tree still emitted for expression/materialized reduces. As a result the archived `133604/summary.md` labels maskagg C++ and its twin **in-order**, while their actual C++ and plans say K-lanes(8), and the code combines eight partial chains. The dot twin in item 1 uses that same older form. This is more than cosmetic: the serial-repro acceptance gate reuses this incomplete count (`run.py:1120-1126`), so it could miss a leaked scalar K-lane fold. Add structured fold-form evidence for every emitted kind (and cross-check the artifact), plus synthetic positive/negative acceptance cases for scalar lanes, vector helpers, macro SIMD, LLVM reassoc, and ordered per-cell tiles. Extend the 'no fold reordering under repro' check to mt8 as well; current check is explicitly serial-only. Threaded maps and ordered tiles must remain accepted.

**Fold-simd timing script bug is identifiable, and the bad log must stay excluded.** The runner's `scratchpad/p2r3/fold-ab_time.txt` starts with paths such as `.../dot-serial-lanesoff.exe/dot-serial-lanes4.exe`. Original `$exe` overwrote parameter `$Exe` because PowerShell variable names are case-insensitive. The current `scratchpad/ab/ab_time.ps1` and the runner's `ab_time_fix.ps1` use `$exePath`; the former now comments on the bug. The corrected output is `p2r3/fold-ab_time-fixed.txt`. Do not use any timings from the failed script. Both timing scripts should also fail immediately on a missing executable/nonzero native exit/missing checksum, and record raw samples and executable hashes. Their Stopwatch measures process-launch/PowerShell wall time, unlike the benchmark's internal timer; label it accordingly before comparing milliseconds or tiny percentage changes. The fixed table also has changes outside the written '1-3% everywhere' claim (see item 8, correction 8).

**Worktrees are candidates for later housekeeping, not safe-to-delete conclusions.** Read-only ancestry checks show six follow-up tips already contained in this checkout: g32-g34 `2ebc490f`, band-vec `8c66dcb0`, maskagg-compact `54c98afe`, recur-dest `9d03e588`, tri-tiles `dc54e1bf`, fold-simd `4e49f46a`. Their tracked-file status was clean. `blas-repack` `5d1a3814` is NOT an ancestor and contains the unmerged feature. There is also the detached baseline worktree at f2315642 and the older llvm-tiles worktree. These directories contain useful diagnostic binaries and may contain untracked evidence; preserve them until the owner archives that evidence and no longer needs A/Bs. Other unrelated active branches are outside this review. Nothing was removed, pruned or changed.

**Stale explanatory text:** `bench/kernels/maskagg/maskagg.blade:7-10` still says LLVM refuses compound and falls back, despite Round 3 having a real LLVM arm. `src/cpp/blade_fold_lanes.hpp`'s `dot3` comment near the bottom still says the second product is fused with the add under contraction, contradicting the landed rounded-product implementation. Keep the historical Oct-18 register snapshot clearly historical. Fix these alongside the item-8 prose rather than letting future diagnoses inherit obsolete assumptions.

**Tests to schedule with the authorized runner after actual fixes (none run here):**

- Item 1: raw-bit conversion checks across signed-32 boundaries and Int64 extremes, plus a full-size generator performance control and band vectorization checks. Generic Float32/Float64 product-rounding pins must continue passing; do not undo 4e49f46a to recover unrelated generator speed.
- Items 2-3: cancellation-sensitive nested-row checksums with odd extents and 1/2/8 threads; verify exact interpreter bits with contraction off. Include a deliberately unpartitionable global chain that must remain ordered, and a side-effecting map that must decline parallel evaluation.
- Item 4: fused and unfused independent sums with different seeds, empty inputs, signed zero, alias/mutation boundaries and required rounded products; inspect input/output stores in benchmark artifacts.
- Item 5: all/none/alternating/random masks, last element rejected (spare-slot behavior), keyed reads after compaction, and empty unseeded reduce's BL8003; both lanes and optimizer on/off.
- Item 6: mixed-licence joins, noncommutative legs, tiny extents, actual-team sizes; pointwise recursive trajectories versus neighbor-dependent recurrences, with correct barriers and unchanged checksums.
- Item 7: unmerged preference-route tests under poisoned env/toolchain settings and explicit restoration; preserve the native-preempt acceptance proof.

G29's generic compact-operand BL9002 remains listed as an open correctness issue in the register. I did not reproduce or silently close it during this performance review; retain it as a separate compiler issue rather than losing it when the benchmark round closes.

**Recommendation:** fix evidence classification and fail-fast diagnostics, archive corrected timing provenance and stale comments **this round (small)**; implement the focused correctness/performance tests with their respective fixes **later (medium)**; leave worktree cleanup to an explicitly authorized housekeeping task.
