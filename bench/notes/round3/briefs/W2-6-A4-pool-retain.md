# W2-6 -- A4: the pool-retention policy as an OPT-IN arm (`BLADE_POOL_RETAIN`), measured beside the default

Read `BUILDER-PREAMBLE.md` first (its gap-register range is stale: the register reaches G68/G69 on the integration
branch; this item does not open a new row -- it updates **G52**). Your worktree is cut from the integration tip named in
your task. The user's decision (2026-10-21): "build the retention policy as an opt-in arm (BLADE_POOL_RETAIN), measured
beside the default; no silent default change." Research: `round3/research/A-census.md` Part 2 rows 4-5 (recur mt8), the
A4 rows of sections 5-7 (`**A4 (retention).**` ~207-210; the hook row ~251; the DROP row ~266); attack review
`round3/reviews/codex-review-A.md` finding 6 (its corrections are part of this contract, below) and its verdict line
"A4 -- MEASURE FIRST: separate retention-policy arm ... no silent default change". Wave-3 record: gap row G52
(`bench/notes/gap-register.md`; the probe arm `kMaxBlock` 64 MB / `kBudget` 96 MB measured recur T=8 program end
11.5-11.8 -> 7.9-8.2 ms, stencil T=8 9.7-10.2 -> 9.3-9.4), `bench/notes/wave3/recur-mt8-attribution.md` section 3,
`stencil-mt8-attribution.md`, `bench/README.md` item 6 (the protocol asymmetry: the Fortran experts deallocate after
their stop line; Blade frees a kernel's large pool inside its timed region).

## What exists

C++ lane: `src/cpp/nested_array_utilities.hpp` `blade_pool` -- a per-thread (thread_local) block cache with
`kMaxBlock = 4 MB` / `kBudget = 8 MB` constexpr (~104-115; `kMaxBlock <= kBudget`); a released block above kMaxBlock
goes straight to `delete[]` (so recur's 48 MB trajectory and stencil's 24 MB `lap` are freed inside the timer);
retention lasts until reuse, eviction (oldest slot first), flush or THREAD EXIT -- not unconditionally until process
exit; `BLADE_POOL_CACHE_OFF` (memcheck / ASan builds) disables the cache; `BLADE_POOL_CACHE_STATS=1` (test builds)
counts fresh / reused / retained / evicted. LLVM lane: `src/cpp/blade_llvm_shim.c` ~159-200 is the twin allocator
(64-byte aligned pools with a header; "freed pools stay COMMITTED here and go back to the next request of the SAME
rounded byte size"; off under BLADE_POOL_CACHE_OFF) -- FIND its size policy (does it cap retained pools, per thread or
process-wide?) and state it in your report; the arm must be applied and recorded in BOTH lane runtimes (review A/6).
Build flags: `Build.fs optFlags` ~107 / `llvmOptFlags` ~140; the run record carries the FP policy and library routes
as `-DBLADE_RR_*` build defines (`src/cpp/blade_run_record.hpp`). The exe cache keys on the EMITTED TEXT and the CPU --
NOT on arbitrary compiler flags: verify this (`Build.fs` / the cache key) before choosing how to pass the policy.

## The arm

`BLADE_POOL_RETAIN`: unset / `0` / `off` = the default policy (4 MB / 8 MB, unchanged, byte-for-byte the current
emission apart from the policy line below); `1` / `on` = the measured probe's retention (kMaxBlock 64 MB, kBudget
96 MB per thread); `<maxMB>:<budgetMB>` = an explicit pair for A/B sweeps (validated: max <= budget, both positive,
a malformed value = the default with a warning line). A toolchain key of the same name (`blade setup` persists an env
value; the env wins). Delivery: the SAFE route is to put the policy into the emitted text (a `#define
BLADE_POOL_MAX_BLOCK ...` / `BLADE_POOL_BUDGET ...` pair ahead of the runtime include in the generated .cpp, with the
header's constexprs reading the defines when present and keeping today's values otherwise), so the exe cache keys on it
for free; for the LLVM lane the shim's compile must carry the same defines AND they must enter the shim identity hash
(as blade_member.h's deployment did) -- if you pass flags instead, show that the cache key includes them. Record the
policy in the run record (`-DBLADE_RR_POOL_RETAIN=...` or the emitted define) so the harness knows which arm ran, and
in `blade plan`'s build line (the line that names the compiler flags / FP policy: add `pool retention: default (4 MB /
8 MB per thread)` or `BLADE_POOL_RETAIN=1 (64 MB / 96 MB per thread)`). Register in `CliSelfTests.fs suiteClearedKnobs`.
No value, schedule or store changes: every test's output is identical under either policy; `memcheck` builds keep the
cache OFF under either.

Honesty (review A/6, carry these into the record and the docs): retention relocates destruction out of the timer, it
is not less total application work; the budget is PER THREAD, so T threads may retain T x budget; retention lasts until
reuse / eviction / thread exit, not until process exit; the harness's peak working set (`run.py` reads
PeakWorkingSetSize after exit) can RISE under the arm without any page reuse (retaining A while B allocates), and can
also stay unchanged (keeping the last allocation alive longer need not change an attained peak) -- the A/B reports
kernel time, wall_s AND peak_working_set per arm, never the time alone.

## Tests

- The pool-cache block (find it: the cases that set BLADE_POOL_CACHE_STATS / read the `[blade-pool]` line): under the
  arm a 48 MB release is `retained` (not freed) and `evicted` at thread exit; under the default it is freed inside; a
  budget smaller than the block keeps today's path; the `<max>:<budget>` parser (valid pair, max > budget refused,
  garbage = default + warning); the policy line in `blade plan`; the run-record field; the emitted define present only
  when the knob is set (default emission unchanged byte-for-byte vs the tip apart from nothing -- assert the default
  build's .cpp is identical to the tip's for one program); LLVM: the shim built with the defines, the identity hash
  changing with the policy, the same retained/evicted observation if its allocator exposes counters (else say so and
  record the LLVM policy as "retains by rounded size, uncapped / capped at N" as you find it).
- `cli` plan needles; `docs`; `corpus-wiring`; `blade test memcheck` smoke if it exists (cache off under both).
- Directional (small load, allowed once): `blade run` of `bench/kernels/recur/recur.blade` at full size, T=8 (mt8
  licensed), default vs `BLADE_POOL_RETAIN=1`, 5 interleaved pairs, whole-program `completed in` medians -- expected
  ~11.5 -> ~8 ms; and stencil once. The wave-end harness A/B (the orchestrator's, mirror `r3w2retain`) is the measured
  record: recur / stencil mt8 licensed + repro with peak_working_set per arm.

## Records / docs

Gap row G52 (BUILT as an opt-in arm, the user's decision, the directional numbers, the honesty paragraph, "harness A/B
pending"); CLAUDE.md env-table row (`BLADE_POOL_RETAIN`); `bench/README.md` item 6: one paragraph naming the arm as a
RECORDED runtime-policy arm beside the default (never the default); `docs/features.md` if it lists the pool cache.

## Not in scope

Changing the default; A5 (lagged in-team row folds); the harness A/B; any kernel source or twin.

## Report

The commit sha (ONE squashed commit on your branch on top of the tip; both co-author lines), files per lane, the LLVM
allocator's policy as found, how the policy reaches the exe cache key and the run record, the directional numbers,
footers for every block run (the pool block, cli, docs, corpus-wiring, llvm facts / shim, memcheck if run), what you
could not verify.
