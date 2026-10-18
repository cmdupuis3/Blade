# Methodology review

**Recommendation: do not publish performance verdicts under the current protocol.** The suite is unusually candid about compiler limitations, but several measurement and validation rules could still produce misleading results in either direction.

This was a read-only review. I ran no benchmarks and changed no files. Findings about the harness describe the inspected draft, not a completed implementation. File paths below are relative to `bench/`.

## 1. Fairness of the fight

**M1 — SEVERITY: major — The “natural” tier is sometimes deliberately less natural than its label claims.**

**At issue:** `README.md` §2: “every logical cell computed independently” and “symmetry exploitation of any kind belongs to the expert tier.”

A competent programmer can recognize symmetry without writing packed offsets or a microkernel. The restriction is defensible as a **dense, symmetry-unaware baseline**, but not as a general characterization of natural Fortran.

| Kernel/spec | Assessment and concrete correction |
|---|---|
| `gram/spec.md` | `MATMUL(TRANSPOSE(x),x)` is natural. Preserve the intrinsic baseline; do not select a slower explicit square nest merely because it exposes the expected Blade win. |
| `comoment3/spec.md` | A full cube is a plausible literal translation, but exploiting the explicitly stated permutation symmetry is also ordinary scientific programming. Label the dense tier accordingly. |
| `antisym/spec.md` | Computing both triangles independently is questionable when antisymmetry and the zero diagonal are explicit. An upper triangle plus mirrored assignment is a natural dense implementation. |
| `pairkernel/spec.md` | Likewise, triangular evaluation plus mirror copies is common for a symmetric kernel matrix. The forced full evaluation exaggerates the gap to “natural” code. |
| `fusedchain/spec.md` | Separate array statements are credible. A single array expression or straightforward fused loop is also natural when only a scalar is requested. Keep the staged baseline, but name it. |
| `onepass/spec.md` | Four `SUM` expressions are credible; a loop with four accumulators is also routine. This measures explicit fusion, not necessarily expert-level programming. |
| `stencil/spec.md` | Array sections versus a contiguous explicit nest are fair counterparts. Do not assume the array expression creates a temporary. |
| `recur/spec.md` | Array RK4 versus scalar-stage fusion is credible. Requiring the full trajectory is fair because it is an explicit output. |
| `maskagg/spec.md` | `PACK` is appropriate because the subsequence is an output. A masked sum would implement a different output contract. |
| `dot/spec.md` | `DOT_PRODUCT` versus an explicit loop is fair. |
| `saxpy/spec.md` | Array assignment versus an explicit loop is fair, subject to proving that output stores survive optimization. |

**Fix:** Rename the constrained tier, or permit ordinary symmetry-aware dense implementations. Keep expert Fortran as the principal comparison and document how each implementation was selected.

**M2 — SEVERITY: note — The algorithm rule is defensible, but its boundary needs precision.**

**At issue:** `README.md` §1.1: “same algorithm, same asymptotic work,” with symmetry exploitation, blocking, packing and checksum fusion allowed.

These freedoms are reasonable for testing whether declarations recover hand-written optimizations. The inspected `gram/expert.f90` register blocking and timed panel packing are legitimate. Different storage orientation is also ordinary implementation freedom, rather than a special concession to Fortran.

**Fix:** Define equivalence by required outputs, arithmetic dependencies and permitted symmetry reduction. Explicitly exclude algebraic shortcuts that compute only the checksum—for example, deriving a matrix norm without producing the matrix. Record extra packing work and memory. Describe expert code as the reviewed implementation, not a proven fastest possible implementation.

**M3 — SEVERITY: major — Strict floating-point compliance is documented but not enforced by the harness.**

**At issue:** `README.md` §3 still requires a serial build “without `-fopenmp`,” despite D23 and both acceptance reports establishing that Blade always passes it. In `harness/run.py::_build_blade`, `simd_reductions` is recorded but never rejected for strict serial builds.

The known top-level fold path can emit `BLADE_OMP_SIMD_REDUCTION` in a serial build. Detecting thread constructs does not detect that reassociation. The shipped `config.json` correctly supplies the ifx contraction workaround and SIMD-only flags, but missing configuration silently restores older, non-equivalent defaults.

**Fix:** Remove the stale no-OpenMP argument. Fail strict serial acceptance on unauthorized reduction reassociation, checking preprocessed C++/optimized IR and representative assembly. Require the authoritative configuration. Retain the documented ifx probes, but rerun them for the final compiler version and actual flag set. “FMA permitted” must not be described as proof that every compiler contracts every relevant expression identically.

**M4 — SEVERITY: major — The fast regimes grant different optimization permissions.**

**At issue:** `README.md` §3 compares `BLADE_FP_REASSOC=1` with gfortran `-ffast-math` and ifx `/fp:fast`.

Blade’s knob licenses selected reduction transformations. GNU fast math additionally permits reciprocal transformations, finite-only assumptions and other changes. This particularly affects division in `fusedchain`. The harness merely warns if LLVM emits no reassociation flags, while retaining the fast label. [GCC documents these broader permissions.](https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html)

**Fix:** Separate “reassociation-enabled” from “compiler fast-math defaults,” or explicitly label the latter as unequal permission sets constrained by an accuracy test. Record actual transformations and compare fast answers against an independently validated strict reference. Do not assert that the flag names establish equivalence.

**M5 — SEVERITY: major — Some excluded parallel comparisons conceal a Blade limitation.**

**At issue:** `notes/kernel-list.md` calls `recur` and `maskagg` “sequential by construction in both languages”; `recur/spec.md` excludes mt8 because Blade lacks a licence slot.

The RK4 trajectories are independent across `x`; ordinary Fortran can parallelize them while preserving each trajectory’s arithmetic order. Stable compaction can also be parallelized, although excluding prefix-sum compaction under the same-algorithm rule is defensible.

**Fix:** Report Fortran mt8 recurrence results with Blade marked “no parallel form,” as already planned for `onepass`. Describe `maskagg` as restricted to sequential compaction by protocol, not inherently sequential.

## 2. Input generation

**M6 — SEVERITY: major — The generator is neither independent across arrays nor uniform over its integer output space.**

**At issue:** `README.md` §5: seeds `10000019 * j` mean “no two inputs are shifted copies of each other.”

Writing the entire generator as \(F(k+s)\) gives exactly

\[
G(k,s_2)=G(k+s_2-s_1,s_1).
\]

Different arrays are separated segments of one sequence. Non-overlapping segments can be adequate, but they are not independently keyed streams.

There is also a provable low-bit bias. Over a complete uniformly distributed `h1` domain, write `h1 = 4096q+r`. When `q` is odd, `h1*(q+1)` is always even; when `q` is even, it is even half the time. Thus `h2` is even 75% of the time and `h3` is odd 75% of the time.

**Fix:** Correct both claims. Either adopt a documented counter-based generator with separate stream keys, or validate this generator for the actual workloads: cross-array correlations, row-stride correlations, mask acceptance/run lengths, stencil responses and pair-distance distributions at the specified sizes. The low-bit bias alone does not invalidate the floating-point workloads, but mean/variance and three short lags are insufficient evidence.

**M7 — SEVERITY: minor — Exact input conversion is sound; the integer bound is wrong.**

**At issue:** `README.md` §5 claims “every intermediate below \(2^{56}\).”

The final affine product `h2 * 1103515245 + 12345` can approach \(2.37\times10^{18}\), requiring a bound below \(2^{62}\), not \(2^{56}\). It still fits signed Int64. `bench_support.f90` uses the safer bound.

For the actual `[0,1)` and `[0,2)` ranges, conversion and binary scaling are exact, so FMA differences cannot change input cells.

**Fix:** Publish bounds for all three rounds and the permitted `k+seed` domain. Retain the dyadic ranges. Distinguish exact individual inputs from exact accumulation of their sum; prove the latter’s integer-mantissa bound for each size if exact checksum comparison remains required.

**M8 — SEVERITY: major — Identical generator source mathematics does not imply identical generation cost.**

**At issue:** D9 says the total ratio is pulled toward one because “both arms pay the same input cost.” `bench_support.f90` explicitly uses bit masking and shifting; Blade spells remainder and division.

Compilers may lower these identically, or differ in vectorization, signed arithmetic handling, inlining and constant propagation. Fixed seeds and extents also permit precomputation, especially at smoke sizes. There is no general guarantee against compiler exploitation of deterministic input construction.

**Fix:** Inspect optimized generation loops, including input-only twins. Report generation cost per arm instead of assuming a common cost. Prefer a common runtime-provided seed, recorded per run, while retaining static extents. Confirm that inputs remain materialized in both the total and twin executables.

**M9 — SEVERITY: blocker — Difference of medians is not an identified kernel-only time.**

**At issue:** `README.md` §5 and D9 define `net = median(total) − median(inputonly)`; `dot/spec.md` says “the net figure is the result.”

The difference includes more than the kernel:

- The total reads `input_checksum` after the kernel; the twin reads it immediately after generation, with different cache contents.
- Removing the consumer can change generation, allocation, fusion and dead-store optimization.
- Output allocation and first touch occur only in the total.
- Input generation may initialize an OpenMP team and alter frequency before the kernel.
- `TWIN_OF` shares one Fortran twin between natural and expert despite potentially different generation layouts and schedules.

These effects can bias either language; more rounds do not remove them.

**Fix:** Keep the complete timed region as the primary estimand. Label subtraction an **incremental total-minus-twin estimate**, not kernel time. If a kernel-only claim is required, add common phase timing after materialized input preparation and specify its cache/first-touch state. Require a matched twin for each materially different preparation path.

## 3. Checksums

**M10 — SEVERITY: blocker — Source-level materialization does not prove that the executable stores the output.**

**At issue:** `README.md` §1.5: checksum fusion is allowed but “the stores must happen”; `saxpy/spec.md` treats `|> compute` plus an emit check as sufficient.

A compiler can eliminate stores whose only subsequent use is a checksum it has fused into production. Fortran `SUM` over an expression can stream without retaining the named array; g++/clang can similarly optimize Blade’s emitted allocation and stores. Sampling only some output cells would increase this risk.

**Fix:** Make the output buffer observable to a separately compiled opaque consumer, without cross-boundary LTO, using an equivalent mechanism for every arm. Place the boundary so all computation and stores must precede the stop clock. Validate optimized machine code for full-size executables, not only emitted C++ or unoptimized LLVM. If such observability is unavailable, restrict claims to scalar-checksum computation rather than materialized-array production.

**M11 — SEVERITY: major — The checksums do not validate several required properties.**

**At issue:** `README.md` says `input_checksum` “proves” identical inputs; D17 accepts an unverified compaction order.

A sum cannot detect input permutation, transposition or exchange of equal-shaped arrays. Output sum-of-squares cannot detect:

- Global sign reversal of `antisym`;
- Output permutations;
- Incorrect stable-compaction order;
- Some compensating errors between `onepass` statistics.

Adding count to a norm does not independently verify count. Non-negative summands avoid cancellation in the *checksum accumulation*, but do not guarantee a universal `1e-12` error bound.

**Fix:** Add index-sensitive input fingerprints per array. At smoke sizes, compare every logical output cell against an independent implementation. At full sizes, retain the full norm and verify fixed, independently selected cell values, including sign, diagonals, boundary cells and permutation classes. Check compaction count separately and validate ordered samples. Preserve the independent reference code currently mentioned only in probe scratchpads.

**M12 — SEVERITY: blocker — Failed reference arms can still determine reported ratios.**

**At issue:** `harness/run.py::run_block`, `_check_sample` and `_block_verified`.

Concrete defects:

- `REFERENCE_PRIORITY` makes Blade C++ the correctness authority without consulting the independent probe references.
- `_check_sample` never rechecks `input_checksum` during measured rounds.
- A later checksum failure becomes `unstable`; `_status_text` still begins its checksum column with `ok`.
- Ratios can use the surviving earlier samples of a denominator that subsequently failed.
- Net ratios check the numerator and its twin’s status, but do not equivalently require valid reference-total and reference-twin status.
- `_block_verified` only requires the chosen reference arm to be OK, so disagreement in other serial arms does not prevent the threaded gate.
- Comparing every arm only to one reference does not implement the promised every-pair tolerance.

**Fix:** Use independently pinned correctness references and validate both checksums on every run. Any correctness failure must invalidate that arm’s entire performance inference and every ratio depending on it. Gate each threaded arm on its corresponding validated serial arm. Implement the stated pairwise rule or replace it with an explicit oracle-relative rule.

**M13 — SEVERITY: major — D3 conflates validation overhead with a measured compiler defect.**

**At issue:** D3 and `comoment3/spec.md`: the estimated checksum tax “must subtract” from a loss diagnosis.

The missing canonical traversal is a real limitation. But the estimated 10–20% is not a measured additive component that can be subtracted from an observed result. Checksumming changes cache behavior and scheduling; fused Fortran checksumming also changes the producer.

**Fix:** Retain the current total as an honest result, with separately measured phase evidence if available. Do not subtract a planning estimate.

A better design is full validation outside the kernel timer, combined with the opaque output-observation boundary in M10. A cheaper timed check could use fixed pseudorandom logical-cell samples plus a full norm accumulated during production, **if both languages can express it**. Risks remain: sampling misses sparse errors; norms miss permutations and signs; producer accumulation does not force stores. Without a canonical-fold primitive, Blade currently has no demonstrated cheap full statistic that removes D3’s cost. Do not invent one through an unequal benchmark-only shortcut.

## 4. Statistics

**M14 — SEVERITY: blocker — The clock contract is contradicted by the acceptance evidence.**

**At issue:** `README.md` §5 promises monotonic timing; `fortran/bench_support.f90` uses QPC. `notes/llvm-acceptance-A.md`, F8 reports Blade C++ using `high_resolution_clock` aliased to system time and LLVM using `timespec_get(TIME_UTC)`.

Resolution is not the same as monotonicity. Wall-clock adjustment can distort a sample. Additionally, `parse_contract` accepts finite negative elapsed times.

QPC is suitable for 5–25 ms measurements; timer resolution is not the principal obstacle at those durations. [Microsoft recommends QPC for high-resolution interval measurement.](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)

**Fix:** Use a verified monotonic clock in all lanes, ideally the same QPC implementation on this host. Reject nonpositive measured durations from inference. Correct the protocol and support-module assertion that every lane already uses the same clock.

**M15 — SEVERITY: major — Rotation and small sample counts do not adequately support narrow performance claims.**

**At issue:** `README.md` §6 specifies 5/7/11 rounds; `run_block` rotates a fixed arm ordering.

With six main arms and four twins, eleven rounds are not position-balanced; five or seven rounds can leave substantial imbalance. Cyclic rotation also preserves most predecessor relationships, relevant to residual thermal load. Independent bootstrap resampling discards the round structure introduced to control drift.

**Fix:** Use randomized, reproducibly seeded balanced blocks, including twins, and resample whole rounds jointly. Preserve shared-twin correlations. Choose the production sample count from a separate pilot and a declared precision target; approximately 30–50 observations per arm across multiple sessions is a reasonable starting point for short kernels, not a guarantee. Do not stop when significance first appears. Enforce minimum rounds: the current `--rounds` override can bypass them.

**M16 — SEVERITY: blocker — The net-ratio bootstrap conditions away problematic denominators.**

**At issue:** `harness/run.py::bootstrap_net`:

```python
if dr > 0:
    ratios.append(d / dr)
```

An interval is reported when only half the resamples need survive. Discarding nonpositive reference differences conditions the bootstrap distribution on a favorable event. The resulting interval is not the advertised unconditional 95% interval.

**Fix:** Report net differences and their intervals first. If reference net time is not clearly separated from zero, mark the net ratio undefined/inconclusive. Never discard denominator draws to manufacture a finite interval. Jointly resample matched rounds and all shared components.

**M17 — SEVERITY: major — “CI excludes one” and “parity” overstate the conclusions.**

**At issue:** D12 and `verdict_of`: any interval above one is a win; an interval containing one inside `[0.8,1.25]` is parity.

A tiny statistically detectable difference can be practically irrelevant. Conversely, failure to detect a difference is not evidence of equivalence. The parity band permits large differences, and dozens of comparisons create multiple-testing concerns.

**Fix:** Predeclare a practically meaningful effect threshold and distinguish “detectable difference,” “practical win/loss,” “equivalent within ±δ,” and “inconclusive.” An equivalence interval need not contain one. State whether intervals are pointwise or simultaneous; apply an appropriate correction for suite-wide superiority claims.

**M18 — SEVERITY: major — Drift monitoring misses important failure modes.**

**At issue:** D10 and `_drift_verdict` compare only medians between kernel/regime blocks with a 15% threshold.

A single-kernel session cannot trigger this check. Substantial within-block drift can disappear in the block median. Fifteen percent is too permissive for a claimed 5% effect. Control failures and extreme control durations are collected without the normal thermal checks. The control also allocates/writes a 48 MB trajectory; “L2-resident” describes a step, not the whole timed region.

**Fix:** Check within-block trends and rolling control behavior, validate every control run, and suppress inference for affected blocks. Predeclare a drift threshold consistent with the effect size being claimed. Add a short memory-sensitive control or explicitly limit what the existing control can diagnose. Avoid treating a missing control as merely an informational flag.

**M19 — SEVERITY: major — The power cap is valid only as a recorded experimental condition; safety enforcement is incomplete.**

**At issue:** `preflight` captures the active plan once; `run.py` defaults to a 60-second child timeout. `_check_sample` returns on the 2.5-second DQ condition before checking the threefold thermal slowdown.

An 80% maximum processor state does not establish a constant clock or exactly 80% throughput. It can change the balance between compute-bound and memory-bound kernels, so it may change ratios. Microsoft defines maximum performance state and boost selection as separate controls. [Power-state documentation.](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/static-configuration-options-for-the-performance-state-engine)

**Fix:** Fix the cap before pilot measurements and report results as specific to that capped configuration. Capture processor settings, boost policy, OEM thermal mode and observed effective clocks; do not extrapolate to uncapped performance. Recheck AC status during the session. Abort on thermal excursions, including warm-ups and control runs; do not merely drop the slow arm. Use a bounded watchdog consistent with the run limit. A safe uncapped comparison is optional, not required.

**M20 — SEVERITY: major — The experiment needs a precise fresh-process and affinity definition.**

**At issue:** D13 assumes `OMP_PROC_BIND=close OMP_PLACES=cores` establishes one thread per physical core; D15 and several specs describe the controls as pure bandwidth measurements.

The environment requests placement but does not demonstrate it under both OpenMP runtimes. Serial arms are unpinned. Each sample launches a new process, so the discarded warm-up cannot retain its thread pool, heap or private pages for the next sample. Strict checksum chains may themselves dominate an otherwise bandwidth-oriented kernel.

**Fix:** Verify actual thread count and CPU placement; pin serial arms consistently and keep SMT siblings free of competing benchmark work. Set and record `OMP_DYNAMIC=FALSE`. Describe these as fresh-process, allocation-inclusive measurements unless the design changes. Record OS/background-load conditions, page-fault behavior and runtime startup inside the region. Keep ASLR consistent rather than selectively disabling it. On this single-socket machine, remote-NUMA placement is unlikely to be the main issue, but demand-zero pages and first touch still matter.

## 5. Benchmark gaming in both directions

**M21 — SEVERITY: major — Matching clock placement requires matching allocation lifetime, not just the start line.**

**At issue:** `README.md` §1.6 and §5 include allocation; Blade arrays live inside functions/blocks, while inspected Fortran `gram` arrays remain allocated after `bench_stop_and_report`.

Blade may destroy function-local arrays before its stop clock, whereas Fortran can defer cleanup until program exit. Initial zero-fills, temporary allocation, panel packing and first-touch work can also differ. These are legitimate implementation costs only if the measured endpoint is explicit.

**Fix:** Define whether destruction is inside or outside the region, then implement that endpoint consistently. Audit allocation, initialization, packing, kernel, validation and destruction in optimized executables. Record peak live data, including temporaries and panels; distinguish that from process RSS and reserved address space. Printing after the clock and excluding process startup are otherwise defensible choices.

**M22 — SEVERITY: major — “No library calls” needs an explicit compiler-runtime exception and routing audit.**

**At issue:** `README.md` §1.4 prohibits libraries; D7 permits libgfortran’s `MATMUL`; the flag table does not explicitly disable Intel matrix-loop substitution.

A blocked intrinsic implementation is natural Fortran. Disabling it solely because it beats Blade would weaken the baseline. Conversely, unreported external BLAS or automatically threaded substitution violates the declared regime.

**Fix:** Define native as “no user-selected external BLAS,” with compiler intrinsic/math runtimes allowed and disclosed—or create separate runtime-enabled and restricted-loop comparisons. Apply and record these controls:

| Route | Required control |
|---|---|
| GNU external BLAS | Explicit `-fno-external-blas -fno-external-blas64`; inspect link maps/calls. |
| GNU intrinsic `MATMUL` | Record `-finline-matmul-limit` behavior; retain the ordinary runtime implementation. |
| Intel matrix-loop substitution | Explicit `/Qopt-matmul-` in the restricted native regime. |
| Intel automatic threading | Keep `/Qparallel` and `/Qpar` absent; verify final thread behavior. `/O3` alone is not evidence of automatic parallelization. |
| Explicit BLAS regime | Verify actual `dsyrk` routing, identical library/build, integer ABI and effective library thread count. |

GNU documents external BLAS as a separate option. Intel documents `/Qopt-matmul-` as disabling substitution and `/Qparallel` as off by default. Confirm these behaviors on the installed 2026.1 compiler rather than importing old ifort assumptions. [GNU options](https://gcc.gnu.org/onlinedocs/gfortran/Code-Gen-Options.html), [Intel MATMUL option](https://www.intel.com/content/www/us/en/docs/fortran-compiler/developer-guide-reference/2025-3/qopt-matmul-qopt-matmul.html), [Intel parallel option](https://www.intel.com/content/www/us/en/docs/fortran-compiler/developer-guide-reference/2025-1/parallel-qparallel.html).

**M23 — SEVERITY: note — `-march=native` and `/QxHost` are reasonable choices, not identical tuning contracts.**

**At issue:** The flag-equivalence claim in `README.md` §3.

The local AVX2/FMA probe is useful evidence that ifx does not simply fail or take an obvious baseline-only path. It does not establish identical vector width, scheduling, math-library dispatch or treatment of every kernel. Intel defines `/QxHost` in terms of host instruction availability. [Intel documentation.](https://www.intel.com/content/www/us/en/docs/fortran-compiler/developer-guide-reference/2025-3/xhost-qxhost.html)

**Fix:** Retain native targeting, archive target-feature and assembly evidence, and identify scalar/vector math calls for `pairkernel`. If a common-ISA sensitivity experiment is needed, predeclare it—for example GNU/Clang AVX2+FMA versus ifx `/arch:AVX2`. Do not select whichever target flag gives the preferred result.

**M24 — SEVERITY: major — A parallel input loop can masquerade as a parallel kernel.**

**At issue:** `_build_blade` counts thread constructs across the whole artifact. `onepass.blade` has a parallel input generator but no parallel statistics fold. `gram/natural.f90` similarly threads input generation while leaving `MATMUL` unannotated.

Consequently, “thread constructs > 0” is not evidence that the operation under test runs on eight threads.

**Fix:** Record parallelism separately for generation, kernel and checksums. Require kernel-specific evidence for an mt8 kernel claim. Report “parallel preparation, serial kernel” where applicable. LLVM fallback detection’s requirement for positive `.ll` evidence is a good safeguard; extend equivalent evidence requirements to BLAS routing and kernel threading rather than issuing warnings alone.

**M25 — SEVERITY: minor — Stack and DLL choices are sensible, but their costs and identities need accurate reporting.**

**At issue:** `notes/fortran-notes.md` says the 256 MB stack reserve “costs nothing”; wrappers preserve default temporary placement and arrange runtime DLL lookup.

Reserve is not 256 MB of committed memory, which is good. However, stack commitment, guard-page handling and temporary placement can affect timed work. Main-thread reserve does not enlarge OpenMP worker stacks.

**Fix:** Record reserve and commit sizes, temporary placement and worker stack settings. Do not add `/heap-arrays` or `-fstack-arrays` selectively after observing results. Hash the actual loaded GNU, Intel, Clang and OpenBLAS DLLs, not just import names or adjacent DLLs. Disabling executable caching is harmless for runtime comparison because builds are outside timing, provided build heat is allowed to settle.

## 6. Reporting and reproducibility

**M26 — SEVERITY: major — Kernel selection and hypotheses must be frozen without requiring predetermined losses.**

**At issue:** The charter requires controls Blade “does NOT win”; `recur/spec.md` says a suite that cannot show the predicted loss “is not measuring.” D1/D2/D4 drop or reshape candidates around compiler capability.

Controls are useful because their behavior has an independent explanation, not because they must lose. Dropping `band` and replacing pair sums with matrices also narrows the supported claim.

**Fix:** Report all outcomes, including unexpected control wins, and investigate them without treating the prediction as truth. Include the dropped/refused workload inventory alongside the eleven measured kernels. Freeze sources, sizes, hypotheses and tuning decisions before measurement.

Update stale mechanisms before that freeze: `gap-register.md` G1 and `gram/spec.md` predict six-way jamming, whereas `llvm-acceptance-A.md` §7 reports no such jamming in the actual Gram kernel. Reconcile accepted `prodsum` and stencil-checksum rewrites with the specs.

**M27 — SEVERITY: major — The result archive is not yet sufficient to reproduce or audit a session.**

**At issue:** `run.py::finish` records hashes and paths, while builds live in reusable `.build` directories. Only returned blocks enter `self.blocks`; a thermal exception inside a block loses that block’s accumulated samples.

A hash does not preserve a source file or binary. Subsequent builds can replace referenced artifacts. Imported `bench_support.f90`, generated headers and resolved runtime dependencies are not all captured. Skipped regimes/arms can disappear rather than becoming explicit unrun cells.

**Fix:** Produce an immutable session bundle containing:

- Protocol, specs, configuration, all sources/includes, rewritten size variants and independent reference programs;
- Compiler/linker identities, complete effective commands, build logs, generated code, executable hashes and resolved runtime-library identities;
- Power/affinity settings, seeds, execution order, raw per-run values, failures and aborted partial blocks;
- A complete planned-cell manifest distinguishing unrun, unsupported, fallback, failed and disqualified;
- Exact reproduction commands and a table whose ratio denominator is explicit per kernel.

Checkpoint each run, including warm-up/control evidence, before starting the next. Preserve dirty/uncommitted source content rather than relying on git SHA alone.

**M28 — SEVERITY: minor — Several draft checks accept protocol violations silently.**

**At issue:** `substitute_sizes`, `parse_contract`, `load_config` and arm discovery.

The size rewriter replaces the **last** integer and accepts multiple markers, contrary to the one-literal/one-marker rule. Parsing accepts duplicate timing/checksum lines as extras and tolerates arbitrary additional output across all lanes. Input-only builds need not print `checksum`, despite the specified zero checksum contract. Missing required twins can remain merely unbuilt, allowing totals-only reporting where net reporting was promised.

**Fix:** Reject ambiguous size declarations and duplicate contract fields; permit only the documented LLVM scalar extras. Require and verify twin `checksum = 0.0`. Validate required twins, arm coverage, configuration and successful smoke evidence against the exact source hashes before a production session. These are completion requirements for the draft, not evidence that a final harness was expected already.

## Prioritized blockers and majors

1. **Establish what is actually measured:** monotonic clocks **M14**, observable output stores **M10**, explicit allocation/destruction endpoints **M21**.
2. **Make correctness authoritative:** invalidate failed references and dependent ratios **M12**; add index/sign/order-sensitive validation **M11**.
3. **Repair net-time claims:** stop treating subtraction as isolated kernel time **M9**; remove denominator-conditioned bootstrap intervals **M16**; measure rather than subtract estimated checksum tax **M13**.
4. **Lock down execution regimes:** strict/fast permissions **M3–M4**, compiler-runtime/BLAS routes **M22**, kernel-specific threading evidence **M24**, affinity and fresh-process conditions **M20**.
5. **Make the host/session trustworthy:** power-cap and abort handling **M19**, drift checks **M18**, balanced rounds and adequate replication **M15**, defensible verdicts **M17**.
6. **Remove interpretation bias:** natural-tier naming **M1**, missing parallel baselines **M5**, generator claims and cost assumptions **M6/M8**, frozen selection/hypotheses **M26**, immutable complete reporting **M27**.
