# Methodology review — dispositions

The independent referee (Codex, read-only, 2026-10-17) reviewed the charter, README, kernel
list, design decisions, gap register, all eleven specs, the Fortran support module and
wrappers, the acceptance notes and the harness draft. Its full report is
`review-method-codex.md` (verbatim). Its headline: **do not publish performance verdicts under
the then-current protocol** — five blockers, nineteen majors. This file is the orchestrator's
disposition of every finding, with an owner. Owners apply their items and record what they did
in their own notes; the architect folds the protocol-level outcomes into `README.md`.

Owner codes: ARCH architect · HARN harness · FORT Fortran · BLA blade-A · BLB blade-B ·
ORCH orchestrator · RUN runner.

## Blockers (all accepted)

| # | finding | disposition | owner |
|---|---|---|---|
| M9 | difference of medians is not kernel time | The complete timed region is the PRIMARY estimand. "net" is renamed **incremental (total minus the arm's own twin)**, reported as a difference with its interval first and a ratio second, never called kernel time. Every arm gets its own twin (M8). | ARCH text, HARN |
| M10 | source-level materialization does not prove the executable stores the output | No symmetric opaque-consumer mechanism exists for Blade, so this becomes an **audit gate before publication**: for every arm at full size, inspect the optimized machine code (`objdump -d`) and confirm the production loop stores the output array; record per arm. An arm whose compiler eliminated the stores is reported as "checksum-only computation" and its spec says so. | BLA, BLB (Blade arms), FORT (Fortran arms) |
| M12 | failed reference arms can still determine ratios | The correctness authority is an INDEPENDENT reference: `bench/validation/pins.json` (numpy, see M11), not `blade-cpp`. Both checksums are validated on every run (warm-up, rounds, controls). Any failure invalidates that arm's whole block and every ratio that uses it. Each mt8 arm is gated on its own validated serial arm. The "every-pair" wording is replaced by the explicit oracle-relative rule. | HARN, ARCH text |
| M14 | clocks differ across lanes and are not monotonic | The C++ lane's `TIME` becomes `std::chrono::steady_clock::now()` and the LLVM shim's `blade_now` becomes QueryPerformanceCounter (`clock_gettime(CLOCK_MONOTONIC)` off Windows) — a runtime change in `src/`, rebuilt before the pilot. All lanes then read QPC-backed monotonic clocks (Fortran already does). The parser rejects nonpositive durations. | ORCH (runtime), HARN (parser), ARCH (text) |
| M16 | net-ratio bootstrap discards unfavourable draws | Never discard draws. Report incremental differences with intervals; compute a ratio only when the reference's incremental interval excludes 0, otherwise the cell reads "undefined". Resample whole rounds jointly. | HARN |

## Majors (all accepted, some as labels or audits)

| # | finding | disposition | owner |
|---|---|---|---|
| M1 | "natural" is a dense symmetry-unaware baseline, not all natural Fortran | Arm identifiers stay; README and every table define **natural = dense baseline: the literal transcription of the spec with intrinsics and dense storage, no symmetry exploitation**, and **expert = the reviewed implementation**, never "fastest possible". gram natural keeps `matmul(transpose(x), x)`. Each spec records how both implementations were selected. | ARCH |
| M3 | strict compliance documented, not enforced | Strict serial Blade builds FAIL acceptance if the emitted `.cpp` contains a `SIMD_REDUCTION` expansion or the `.ll` carries `reassoc`; `config.json` is required (no silent defaults); README drops the stale "without -fopenmp". | HARN, ARCH |
| M4 | fast regimes grant unequal permissions | Kept as the LOWEST-priority regime, relabelled **"vendor fast-math vs Blade's reassociation licence — unequal permission sets, accuracy-checked at 1e-8"**. Plus blade-B's F11: in fast the C++ lane does not reassociate folds over deferred computations while the LLVM lane does, so both Blade lanes are reported there. | ARCH |
| M5 | excluded parallel comparisons hide a Blade limitation | `recur` gains an mt8 regime for the Fortran arms (threads over x inside the time loop, each trajectory's arithmetic order preserved); the Blade cells read "no parallel form". `maskagg` is described as restricted to sequential compaction by protocol. | ARCH, FORT, HARN |
| M6 | generator: arrays are segments of one sequence; low-bit bias | README corrected (non-overlapping segments, not independent streams; h3 is odd 75% of the time, affecting only the 2^-31 bit). `bench/validation/generator_check.py` (numpy) measures, at the specs' full sizes: cross-array correlation, row- and column-stride autocorrelation of the 2-D inputs, maskagg's acceptance rate and run lengths, the stencil output distribution, pairkernel's distance histogram. The generator is replaced only if something is degenerate. | ARCH text, BLB script |
| M7 | integer bound text wrong | Bounds published per round: round 1 < 2^62 while k + seed < 2^31; round 2 < 2^50; round 3 < 2^62 (max ≈ 2.37e18). Dyadic ranges kept. | ARCH, ORCH (charter) |
| M8 | identical generator math ≠ identical cost | Generation cost is REPORTED per arm from that arm's own twin; Fortran twins per tier via `-cpp -DBENCH_INPUTONLY` / `/fpp /DBENCH_INPUTONLY` on the same source; `inputonly.blade` serves both Blade lanes. Each owner inspects the optimized generation loop once and notes vectorization differences. | HARN, FORT, BLA/BLB |
| M11 | checksums miss permutation, sign, order | `bench/validation/ref_<kernel>.py` (numpy, independent of Blade and Fortran) regenerates the inputs, computes the full output at smoke size and the pins at smoke AND full size → `pins.json`. Dump variants (`bench/validation/dump/<kernel>_dump.blade`; Fortran `-DBENCH_DUMP` writes the array) are compared cell by cell at smoke size by `validate.py` (Blade C++ lane, both gfortran tiers). | BLA, BLB, FORT, HARN (pins check) |
| M13 | checksum tax is an estimate, not a subtractable component | Totals are the result; nothing is subtracted; D3 restated. | ARCH |
| M15 | rotation and small samples | Seeded random permutation of all arms (twins and control included) each round; bootstrap resamples whole rounds; minimum rounds enforced (an override taints the session); defaults serial 11, memory-bound (< 50 ms) 21, mt8 7; a 3-round full-size pilot sets final counts against a ±5% precision target on the ratio. | HARN, ARCH |
| M17 | "CI excludes 1" overstates | Practical threshold δ = 10%. *win*: interval above 1 and median ratio ≥ 1.10; *detectable*: interval above 1, median < 1.10; *equivalent*: interval within [1/1.10, 1.10]; *loss* symmetric; else *inconclusive*. Intervals are pointwise; no suite-wide superiority claim. | ARCH, HARN |
| M18 | drift monitoring misses within-block drift | Control every round; within-block trend check (first-half vs second-half median > 5% flags the block); rolling control threshold 7%; an invalid control run flags the block; a second, memory-bound control (a 16 MB stream) beside the compute control; README says what each diagnoses. | ARCH, HARN |
| M19 | power cap and safety enforcement | Capture `powercfg /q` processor settings (max state, boost policy) and AC state at session start and per block; abort on thermal excursion including warm-ups and controls (any run > 3x the block's running median); child timeout 3x the 2.5 s cap; results labelled "max processor state capped (~80%)". | HARN |
| M20 | affinity and fresh-process definition | `OMP_DYNAMIC=FALSE`; thread count and placement verified once per OpenMP runtime with a probe (FORT); serial arms pinned to one physical core (affinity mask set on the child at spawn, recorded); mt8 `OMP_PLACES=cores OMP_PROC_BIND=close`; measurements described as fresh-process, allocation-inclusive. | HARN, FORT |
| M21 | allocation lifetime endpoints | Defined: the region includes allocation and first touch of everything the program creates; Blade's block scope frees its arrays inside the region (a few `free` calls — recorded asymmetry); Fortran deallocates after the stop line; peak working set recorded per run. | ARCH, FORT, HARN |
| M22 | library routing | native = **no user-selected external BLAS**; compiler intrinsic runtimes (libgfortran `matmul`, Intel runtime `matmul`, SVML) allowed and DISCLOSED per arm. gfortran: explicit `-fno-external-blas`, `-finline-matmul-limit` default recorded; ifx: whether `/Qopt-matmul` is on by default in 2026.1 and where `matmul` routes, recorded; `/Qparallel` absent, verified by thread count. blas regime: dsyrk routing verified on both sides, identical OpenBLAS DLL hash, `OPENBLAS_NUM_THREADS=1` in serial. | ARCH, FORT |
| M24 | parallel input loop can masquerade as a parallel kernel | `blade plan`'s omp lines recorded per kernel (which loop); Fortran pragma placement documented per kernel and tier; tables say "parallel preparation, serial kernel" where the kernel loop is unthreaded. | HARN, FORT, BLA/BLB |
| M26 | freeze selection and hypotheses | Freeze = a tagged commit before the pilot; README carries the inventory of dropped/recast workloads (band, pair sum, O(1) antisym) beside the eleven kernels; controls restated as independently explained, not predetermined losses; hypothesis mismatches investigated. | ARCH, ORCH |
| M27 | result archive insufficient | Immutable session bundle under `results/`: all sources incl. `bench_support.f90` and rewritten size variants, generated `.cpp`/`.ll`, build logs and command lines, exe and DLL hashes, a planned-cell manifest (unrun / unsupported / fallback / failed / DQ), raw samples incl. warm-ups and controls, power and affinity settings; JSON checkpointed after every run. | HARN |

## Minor / note (accepted)

| # | disposition | owner |
|---|---|---|
| M2 | equivalence defined by outputs, arithmetic dependencies and permitted symmetry reduction; checksum-only shortcuts excluded; packing memory recorded | ARCH |
| M23 | resolved `-march=native` target and vector-math call identities archived per arm (pairkernel: scalar `exp` vs SVML) | FORT, BLA |
| M25 | stack reserve/commit, temporary placement and worker stacks recorded; loaded DLLs hashed by resolved path | HARN, FORT |
| M28 | size rewriter rejects ambiguous markers; duplicate contract lines rejected; only the LLVM scalar extras tolerated; twins must print `checksum = 0.0`; required twins validated before a session | HARN |

## Decisions from the kernel agents' reports (same round)

| item | decision | owner |
|---|---|---|
| blade-A F14: the LLVM lane does no FMA contraction by default (`BLADE_FP_CONTRACT` unset = off there; C++ lane = fast) | `BLADE_FP_CONTRACT=fast` on EVERY LLVM arm — contraction only, the licence every other arm has; recorded in the regime table and `config.json` | ARCH, HARN |
| blade-A: gram and comoment3 type their kernel parameters `T^1`, so the dot loop's trip count is a runtime extent while Fortran sees a parameter | Type them concretely (`Array<Float64 like Obs>`), as antisym and pairkernel already do — the "same information" rule; checksums and plan unchanged | BLA |
| blade-B: maskagg's `compound` builds a hashed index (~40 B per kept cell), est. ~120 MB and 200–300 ms at n = 3,000,017 | Full n cut to **1,000,003** (est. ~40 MB peak); the hashed rank-1 compound index is a gap-register entry; peak working set measured once per kernel at full size by the runner | ARCH, BLB, HARN |
| blade-B: the specs' `reduce(reduce(lap * lap, (+)), (+))` allocates a second 24 MB array (stencil, recur) | The committed per-row `prodsum` under a licensed fold stands; specs synced; the allocation is a gap entry | ARCH |
| blade-B wrote the six `inputonly.blade` twins | blade-B owns them; ownership table updated | ORCH |
