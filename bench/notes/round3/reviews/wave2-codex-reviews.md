# Round-3 wave-2 Codex diff reviews (condensed)

The independent Codex diff review of each wave-2 item (W2-1 two looks; W2-4 SEVEN looks: the third held round 2, the fourth held round 4, the fifth gated rounds 4+5, the sixth gated round 6, the seventh gated round 7 -- DO NOT MERGE on the fold-kernel hole -- after which the user's fallback made the kept inline opt-in; round 8) and the merge-level review of the wave (HOLD on W2-4's lifetime class).

Sections (each is one former file, verbatim): [`wave2-codex-reviews.md#codex-diff-w2-5`](#codex-diff-w2-5), [`wave2-codex-reviews.md#codex-diff-w2-0`](#codex-diff-w2-0), [`wave2-codex-reviews.md#codex-diff-w2-3`](#codex-diff-w2-3), [`wave2-codex-reviews.md#codex-diff-w2-1`](#codex-diff-w2-1), [`wave2-codex-reviews.md#codex-diff-w2-1-2`](#codex-diff-w2-1-2), [`wave2-codex-reviews.md#codex-diff-w2-2`](#codex-diff-w2-2), [`wave2-codex-reviews.md#codex-diff-w2-6`](#codex-diff-w2-6), [`wave2-codex-reviews.md#codex-diff-w2-4`](#codex-diff-w2-4), [`wave2-codex-reviews.md#codex-diff-w2-4-2`](#codex-diff-w2-4-2), [`wave2-codex-reviews.md#codex-diff-w2-4-3`](#codex-diff-w2-4-3), [`wave2-codex-reviews.md#codex-wave2-merged`](#codex-wave2-merged)

---

## codex-diff-W2-5

_formerly `bench/notes/round3/reviews/codex-diff-W2-5.md`_

VERDICT: MERGE AFTER FIXES

Reviewed `git diff 667250cd..91d13a00`; compiler unchanged. Paths below are relative to builder worktree `agent-ad141f871863c77be` at 91d13a00. Read the required CLAUDE/protocol/brief/review C and the original N02 catalog. Independent execution: the authorized bench-fortran binary returned `OK` for haloadjoint, inputonly, both dump sources and the scratch G67 reproducer; ucrt64 first on PATH. `git diff --check` clean. `python -I -B ref_haloadjoint.py` stopped at missing NumPy; no independent oracle execution, runtime validation, build, test, emission or harness run. Existing scratch C++ and check logs were read, not regenerated.

1. **HIGH — mt8 metadata reports threaded folds in repro.** `bench/kernels/haloadjoint/kernel.json:35` says both checksum folds and input sums are threaded in mt8 without qualification. The harness copies this note into BOTH mt8 regimes (`bench/harness/run.py:1843`), but repro serializes these folds; `spec.md:241` already describes that distinction. The requested label is also absent.
   **Exact fix:** replace the note with: **“serial kernel, threaded preparation (adjoint half); primal y and buffer/input fills threaded in both mt8 regimes; loss map, loss fold and seven adjoint passes serial; checksum and input-sum folds threaded only in licensed mt8, serial in repro mt8.”** Keep the primal qualification: calling the entire y+dx workload serial would also be inaccurate.

2. **HIGH — G67's reproducer is not delivered in the repository.** `bench/notes/gap-register.md:115` and `spec.md:11` point only to “W2-5 scratch”; neither `g67_repro.blade` nor `n10c.blade` is in this commit. They exist under the separate `C--Users-cdupu-Documents-GitHub-Blade/.../scratchpad/w25` temp tree, not the review tree. The fallback's gating evidence is consequently not reproducible from the committed artifact (README §6 requires a recorded minimal reproducer).
   **Exact fix:** commit both sources under `bench/notes/repro/`, link exact paths from G67/spec, and record the binary/tree identities and observed diagnostics. Preserve the interpreter's distinct main-checkout binary identity. Do not add a knowingly failing default value test; add canonical/mirrored multi-group pins with the eventual compiler repair.

3. **MEDIUM — the proposed optimization misclassifies user-named storage as compiler-inserted.** `spec.md:265` calls the tape compiler-internal, but `haloadjoint.blade:53` explicitly names and forces `d`; `:64–65` explicitly creates `dw` and binds `lv`. Only d's cotangent and synthesized sweep bookkeeping are compiler-created. The current extra work IS disclosed and neither Fortran twin must acquire these non-output buffers; the problem is the future removal claim under hard constraint 1.
   **Exact fix:** distinguish source-named, AD-required auxiliary d/dw/lv from synthesized cotangents. State that sharing their computations may preserve their stores; do not claim permission to delete the forced d/dw stores under this frozen source. A future `wrt`/VJP source that removes them requires an explicit source/protocol revision and fresh baseline. Required benchmark outputs remain y and dx only.

4. **MEDIUM — “one sin and one cos per cell” is approximate, and five is not an inherent AD cost.** `spec.md:114–117,181–190` and `expert.f90:13` present exact counts. Each block recomputes two overlapping sin values; peeled boundaries duplicate additional sin evaluations. With `B = ceil((n−8)/1024)`, source calls are **sin: n+2B; cos: n−4**, total **2n+2B−4** (2024 smoke; 100136 full), versus natural **3(n−4)** and the archived Blade sweep **5(n−4)**. Blade's four sin evaluations revisit the same interior arguments in y/tape/dw/U1; this is missed sharing across passes, not a mathematical requirement of Jᵀw or an unavoidable five-call lower bound of `ad.grad`. Final machine-code counts remain unaudited.
   **Exact fix:** use “approximately two” in hypotheses/metadata, give the exact overlap count in the spec, and describe five as this emission's redundant primal/transcendental work. Identify destination fusion plus cross-pass primal/CSE sharing as the opportunity; preserve U1–U6 and required stores. Keep estimates distinct from measured runtime-call evidence.

5. **MEDIUM — the catalog-size payload is understated by 2×.** `spec.md:147` says n=1,000,003 requires 16 MB for x/w/y/dx. Four Float64 arrays, one of length n−4, require **32,000,064 bytes**. Adding dw, d and d's cotangent gives **56,000,072 bytes**, plus **7,999,992 bytes** for the current literal image, before metadata/thread scratch. Those components total 64,000,064 bytes if resident together; lifetimes and image residency need the actual audit.
   **Exact fix:** correct 16 MB to 32 MB and replace the automatic “raise to 1,000,003 after G68” promise with a fresh per-arm peak-live/store audit after the fill repair. The shipped 50,021 cap is explicitly compiler-driven, not a demonstrated 64 MB limit, and its current small payload is not a cap violation.

Reference/twin disposition: **no BLOCKER found in the source arithmetic.** The catalog (`new-kernel-catalog.md:85–93`) specifies a contributor order and explicitly preserves the existing reverse order in R mode; it does not supply a different unique stencil/order. `spec.md:38–43` openly freezes the emitted order. This is a defensible Fortran reference: ascending-centre scatter induces descending-offset contributions at each destination, with left then right product updates at −2. Both twins, the oracle and `haloadjoint_order.blade:43–50` implement that chain, skip absent windows, retain the two multiplication trees and start dx at +0. The natural six destination passes are the protocol's disclosed literal baseline, not a claim about the best ordinary Fortran; the expert is free to fuse and does. An ascending-centre serial scatter remains a useful catalog control, not an excuse to require atomics.

All sources cover y's n−4 cells and dx's n cells and retain separate +0 checksum chains before sy+sd; inputonly sources match their input generation/sums. dx's boundary is correctly nonzero in general; dw has four zero boundary cells. Sizes 1013/50021 and the four selected regimes are consistent. Physical stores in optimized timed executables are **not established by this review**: the expert's fused scalar consumption can make output stores dead. Keep the already-declared full-size store audit and independent Fortran review as publication gates; dump builds are not substitutes. The order dump uses numeric `==`, not a raw-bit comparator (notably for signed-zero dw); phrase that result as exact numeric equality unless raw bits were separately audited. Contraction/libm caveats are already disclosed.

Five verified hook claims:

| Claim | Exact hook at reviewed commit | Result / fix |
|---|---|---|
| Two −2 updates; stable descending-offset destination passes | `src/GradSweeps.fs:236–239,356–366` | Holds: product operands differentiated separately; assignments sorted, then one NFor per assignment. Fusion must preserve each cell's ordered additions. |
| Loss-map licence refusal and unwanted w gradient | `src/GradNormalize.fs:127–163,278–279,1057–1075` | Holds for this spelling: gather admits a lambda with no where; fallback rejects the clause; Float64 arrays are DiffArray. This does not prove that every possible AD formulation needs five transcendentals. |
| G67 C++ extents and packed row store | `src/IRLoopStructure.fs:887–893`; `src/CodeGenCuda.fs:3731–3762`; `src/CodeGenLoopNest.fs:2186–2193` | Deduction retains the return's rank/symmetry. C++ peels trailing records by Rank but renders one extent per record (`List.map`), yielding `{7,5}` for rank 3. Dense row-copy explicitly excludes symmetry. Expand extents per axis and implement packed sub-result copying/ownership, or explicitly refuse. Add a length-vs-rank invariant to `CodeGenExprSupport.emitExtentsTable:1522`. |
| G67 LLVM/interpreter failure | `src/EmitLlvm.fs:5809–5815`; `src/Interp/Loops.fs:2069–2088`; `src/Interp/ArrayOps.fs:227` | LLVM compares full output axes against outer loop levels, so this rank-raising compact result declines. Interpreter distinguishes cell versus dense-row writes; compact result needs a matching row path. A successful check is an acceptance/coverage defect, not evidence that Deduce computed the wrong extent. |
| G68 zero literal and fill replacement | `src/Grad.fs:391–403`; `src/GradExpand.fs:1541–1544`; `src/GradCommon.fs:709–711` | Holds: `ExprArrayLit(List.replicate n ...)`, n−4 here. Replace the rank-1 case with `zeroFill(iLit(int64 length))`; preserve shape for higher ranks with shaped/nested fills rather than flattening blindly. The reported `lower` interval includes optimizers (`Lowering.fs:3167,3296–3297`); it does not isolate StaticEval, CSE or the primitive lowering walk. No specific quadratic hotspot is proven by those timings. |

G67: **REAL / BLOCKER for admitting N10 in wave 3, pre-existing and not a haloadjoint arithmetic blocker / fix CodeGenCuda:3733 + CodeGenLoopNest:2186 compact-row handling, LLVM:5814 and interpreter row storage; deduction itself preserves the compact rank.**

G68: **REAL / HIGH compile-scalability defect, pre-existing / replace Grad:403 → GradExpand:1544 literal construction with a shaped fill via GradCommon:709; profile lower subpasses before assigning the super-linear cost specifically to StaticEval/CSE.**

---

## codex-diff-W2-0

_formerly `bench/notes/round3/reviews/codex-diff-W2-0.md`_

VERDICT: MERGE AFTER FIXES

Review of `667250cd..e6d55563`; file/git inspection only. No builds, tests, compiler invocations or timings run. Paths/lines below refer to e6d55563. `git diff --check` is clean; reported test footers are not independently verified.

1. **BLOCKER — the newly selected form's raw-bit contract is not pinned.** `tests/OmpTests.fs:3784-3865` compares printed text. Its cancellation/low-bit probe s1/r1 and NaN probe s3 still take row slots: only the signed-zero s2 switches to scatter. The counts at :3834 correctly assert that split, so this is not a missing DEFAULT scatter needle; it is missing adversarial value coverage on that route. The separate coverage case (:2296-2398) compares a finite checksum's ordinary 15-digit output. Signed zero is covered (-0.0 and reciprocal -inf); NaN payloads are not, and the existing `tests/OrbitScatterTests.fs:15` explicitly promises NaN-equivalence, not raw bits. **Fix:** add rank-2/3, both-admitted fixtures with cancellation and distinct NaN payloads in the stored pool, and capture uint64 representations of the generated results. Compare default / prefer / slots-off scatter / threads-off scatter / true serial walk under REPRO+CONTRACT=off at T=1/4/7; assert each binding's route, not only aggregate counts. Preserve the signed-zero case. This is an unpinned bit-equivalence requirement, not a demonstrated wrong result.

2. **HIGH — the measured hybrid's producer team is not asserted.** `rowSlotSym2` (`tests/OmpTests.fs:998-1002`) has no omp licence on either producer. Consequently `orbit_scatter_precedence_spends_no_team` (:2305) proves that a wholly serial-producer fixture opens no team; it cannot catch loss/change of the producer team in pairkernel/comoment3. **Fix:** retain that consumer-only fixture, and add a sufficiently large stored-pool producer with its own omp licence. Assert its emission/team is identical under default, slots=0 and prefer, while only prefer adds the row-slot team; distinguish census sites. Also compare old-default vs new-prefer emitted text, and old-slots=0 vs new-default after removing only the specified changed census comment. Static inspection finds no producer mutation in this diff, but the requested route regression is unpinned.

3. **HIGH — anti_g's “tile runs on the driver's thread team” record is false for the new positive fixtures.** `CodeGenLoopNest.fs:3494,3547` passes thread *eligibility*. The actual driver (`src/cpp/blade_packed_gemm.hpp:441-444,506`) forms a team only when T>1, with T capped by `(p*p*n)>>20`, and suppresses nested teams. At 29x11 (`tests/LinAlgTests.fs:1863`) that cap is 0; at 61x401 (`tests/OptimizeTests.fs:2645`) it is 1. Both always execute serially, yet the new default changes G=1 to G=4 and the latter plan claims the measured threaded regime. **Fix:** for the unset policy, retain G=1 when the static work cap cannot exceed one; compute that eligibility without overflow, separately from the unchanged driver argument. Use a team-capable shape such as 193x401 for the G=4 positive record/route test; pin the small shape to G=1. Describe remaining runtime caps (OMP_NUM_THREADS=1, nested execution) explicitly as eligibility, not a guaranteed team. Keep explicit anti_g overrides unconditional. The budget itself is unchanged and deterministic: it uses n and the schedule, not p or actual T; 1201x401 sequential 6x8 gives 205,312 B = 200 KiB displayed, G=4. This matches W2-D §D2; teams of 2 and 8 get the same G.

4. **HIGH — current records/docs contradict the new policy.** `bench/notes/gap-register.md:94` still ends “mt8 repro stays G35”; `docs/plans/plan-compact-sym-folds.md:459-460` says G35 wins without the prefer qualification; `docs/features.md:443-444` still says slots=0 keeps the serial walk although it can scatter. G44's new “serial controls agree ... so ... same bits” also infers bit equality from timing agreement. **Fix:** state default scatter / prefer row slots / slots=0 disables slots only; label round-2-only claims as historical and remove the timing-to-bits inference. Fix the anti_g regime wording with finding 3. `CLAUDE.md:212` also needs `false` in the off spellings before saying every other value is default (`Toolchain.parseSwitch` accepts false). The G1/G35/G44 numerical citations themselves match w1-ab: 0.841 [0.811,0.872], 0.843 [0.817,0.885], serial anti 1.010/1.020, mt8 anti 0.952 [0.929,0.987]. Historical research/review snapshots also contain the old precedence (`round3/research/A-census.md:29,162`, `D-orbit-regions.md:51`, `round3/reviews/codex-review-A.md:167`); mark them superseded rather than treating them as current guidance.

| Hook / claim | Verified file:line | Result |
|---|---|---|
| Independent scatter admission, then precedence | `src/CodeGenBinding.fs:4555,4836,4863,5629`; `src/OrbitFold.fs:698` | Holds: recogniseStrict→planStrict plus materialized-pool/capture checks precede rowSlotAdmission. No new admission. New default uses the old slots=0 scatter emitter; its census deliberately differs. |
| prefer restores old row-slot selection; suite clears it | `src/CodeGenExprSupport.fs:2279-2289`; `src/CliSelfTests.fs:94,118` | Old emission branch retained; plan decline wording changes (`src/OrbitFold.fs:897`). Clearing prefer yields NEW default. Five emission fixtures plus two coverage fixtures, CLI and identity arms were adjusted; remaining row-view/refusal fixtures do not depend on old precedence. Full text identity is not asserted. A program also containing an eligible antisym tile needs anti_g=1 to reproduce the whole old emission. |
| Yielded census and LLVM restatement | `src/CodeGenBinding.fs:5621`; `src/CliCommands.fs:1018` | Yield names prefer; broadened prefix prevents the yielded record falling into LLVM's reassociation fallback. LLVM scatter emission is unchanged. |
| C++ anti_g regime/budget | `src/CodeGenLoopNest.fs:3494,3547`; `src/CodeGenExprSupport.fs:2222-2242` | Unset selects budget on eligibility, serial G=1 otherwise; explicit 0/1..8 preserved. Actual-team claim fails as finding 3. |
| LLVM anti_g and byte gates | `src/EmitLlvm.fs:5453`; `src/cpp/row_contraction_tile_tests.cpp:174-192`; `src/cpp/llvm_tiles_rawbit.c:293-309` | LLVM passes false, default G=1. Existing row-tile memcmp covers explicit G={1,2,3,4,8}, including threaded calls; llvm tiles compares explicit same-G serial drivers with route/scratch checks. These are compositional coverage, not a direct generated default C++ G=4/threaded versus LLVM G=1 comparison. |

(g) Executable emission changes for ANY C++ fold with `chunkable && !fpReassocEnabled() && rowSlotAdmission=Ok && scatterPlan=Some && policy!=prefer` (not restricted to mt8 or the two benchmarks), and ANY admitted C++ antisym tile with unset/defaulted anti_g, thread eligibility and budget G>1 (including statically serial small tiles); LLVM executable emission is unchanged, while decision text also changes outside those predicates.

---

## codex-diff-W2-3

_formerly `bench/notes/round3/reviews/codex-diff-W2-3.md`_

VERDICT: MERGE AFTER FIXES

Reviewed `667250cd..5bd469f7`; line numbers below are at `5bd469f7`. Read-only source/git review; no builds, tests, Blade, or C++ execution. `git diff --check` is clean. Reported test/timing footers were not independently verified.

1. **HIGH — The runtime form choice is unrecorded; the one-worker fallback checks the request, not the actual team.** `src/CodeGenBinding.fs:4587` passes `blade_omp_get_max_threads()`; `src/cpp/blade_orbit_scatter.hpp:282` branches on that argument BEFORE the clamp/parallel region at :241-246. `OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=1` therefore runs the extra-evaluation pull with one actual worker; `OMP_NUM_THREADS=1` runs serial `leaf3`. Dynamic/nested teams can make the same distinction. Ownership still preserves chain order in both cases; this is not an identified bit defect. But normal emission supplies `no_observe` (`CodeGenBinding.fs:4582-4584`), the applied census always advertises a team (:4575), and `blade_run_record.hpp:176-181` records only library routes, not this choice. The ownership/count record (`OrbitFold.fs:946,953`) cannot tell the harness which execution occurred. Even test-mode `teamsz=1` does not distinguish the two forms.

   **Fix:** retain the requested-one fast path, but inside an opened region use the ACTUAL `blade_omp_get_num_threads()==1` to select seeded serial `leaf3` before doing pull work. Record per-site executed form (`serial-leaf3` / `row-pull`), requested and actual team sizes in runtime census/run-record data, including the no-team fast path; distinguish planned counts from observed form. Add restricted-team (`OMP_NUM_THREADS=8`, `OMP_THREAD_LIMIT=1`) and ordinary T=1/3/8 identity/form assertions. Correct the blanket “runtime team of one” claim until implemented.

2. **MEDIUM — The emitted cross-build bit-identity claim exceeds the comparison.** `tests/OrbitScatterTests.fs:446-452` infers cross-build identity from each binary's own walk twin. The actual bridge between binaries is formatted `valueText` equality (:566-574, :626), not bit serialization. Two lanes/builds can each agree with their own twin while differing below printed precision; NaNs explicitly compare equivalent (:468), and no emitted NaN-payload comparison exists. The header `memcmp` leg tests the runtime algorithm, not emitted C++/LLVM wiring. Pull route counts ARE asserted (:581-584), as are threads-off scatter and LLVM scatter (:607-609); this is not a route-less test.

   **Fix:** export exact result bits from the emitted C++ and LLVM cases and compare pull T=1/3/8, pull-off, serial scatter and walk, including one controlled NaN payload and signed zero. Keep the header comparisons of every H cell. Include the ordinary, non-coverage-instrumented pull build. Qualify `docs/features.md:482` with REPRO + CONTRACT=off and the stated NaN-payload scope; its present unconditional “Bitwise ... at every team size” also covers unverified contraction-fast builds.

3. **MEDIUM — Two requested admission regressions are not directly pinned.** `tests/OptimizeTests.fs:2949` covers a random-key writer in an INTERVENING binding; :2953 covers an ordinary writing producer. Neither directly tests a producer's SIDE-TABLE payload, nor a callback writer in a would-be team body. `symmetry/139` covers an intervening write and a parameter pool, not those cases. The checks are wired in source (`Optimize.fs:4100-4103`, `CodeGenBinding.fs:287-300`); no missing party was established.

   **Fix:** add a planner fixture with the producer write present only in its payload, plus a higher-order callback writer/unknown-callback fixture with an otherwise eligible outer request. Assert no `pull3` and the relevant decline or BL4005, alongside a pure control that does pull. Use a constructed IR fixture if the frontend cannot express the producer payload shape. Pin the earlier-top-level-pool scope as well as the existing parameter scope.

Order audit (a/e): for fixed a <= b, the pulls deliver omitted coordinate c=0..a-1 once each; slice a's unchanged `tri<...,false>` delivers c=a..p-1 once each. For a=b, earlier slices deliver (c,a,a) once and the local diagonal once. For a<b, local row/column dedup handles c=a and c=b without duplication; all-equal follows the same diagonal rule. Every application is accumulator-first. No smallest counterexample found. G/B only partition this ascending sequence; no parity-dependent branch exists. The private row buffer writes disjoint H ranges; the region ends before the unchanged `scatter2` middle/outer tail. Atomic claim order changes owners, not accumulators. The test matrix uses FOUR G/B combinations, not their Cartesian product; p=171 exercises all slice lengths through 171 for G=4/B=64. This is a coverage limit, not an order defect.

Work gate: `pullCounts` counts C(p+2,3)+C(p+1,3) READS/f evaluations, not leaf applications; 262144 first admits p=92. The default is explicitly provisional (`OrbitFold.fs:920-931`); the orchestrator's A/B still has to set it. Largest-first claims follow decreasing a(p-a)+(p-a)(p-a+1)/2 work.

Hook verification (five claims):

| Claim | Exact hook | Review |
|---|---|---|
| Existing strict/stored admission precedes pull | `src/OrbitFold.fs:698`; `src/CodeGenBinding.fs:4666-4690` | Existing purity/totality, rank, seed, capture and stored-source checks retained. |
| Five-party audit is populated and unknown placements decline | `src/Lowering.fs:3187-3191`; `src/Optimize.fs:4031,4070-4103,4118-4148`; `src/CodeGenBinding.fs:283-300` | Producer/intervening payloads included; walk includes inner kernels/seeds; outer kernel/seed explicit; parameter boundary documented; callback-aware team checks use the shared table. |
| Exact G35 request/depth gate, before precedence | `src/CodeGenBinding.fs:4899-4944` (before `rowSlotWhy` :4963) | Pull early-return wins. This commit then has G35 > scatter; W2-0 `e6d55563` changes the later default to scatter > G35, yielding pull > scatter > G35 when combined. Rank 2 falls through. W2-0 `prefer` also cannot preempt pull: use pull=0 for that A/B; pin/document the combined hierarchy during integration. |
| Ordered leaf ownership and retained-H tail | `src/cpp/blade_orbit_scatter.hpp:162,229,255-266,278-289` | `leaf3` extraction preserves the old loop; pulls ascending; THIRD=false; completion before identical tail. Requested-one fallback limitation is finding 1. |
| Form/census and lane fallback | `src/CodeGenBinding.fs:4550,4574-4587,4935-4942`; `src/CliCommands.fs:996-1011` | Applied pull and unspent inner requests recorded statically. Threads suppressed: no pull, existing serial scatter/census. LLVM uses its OWN scatter record or declines; interpreter unchanged. |

Coverage (f): pull knob unset is tested with REPRO pinned; the ordinary fastest default intentionally does NOT pull (`OptimizeTests.fs:3007`). Both request placements occur in emitted cases and symmetry/138. `tests/OmpTests.fs:2307` observes `region=s` at the pull site, not an unrelated producer team. Restricted actual teams are explicitly removed from the present tests. Full-suite, harness A/B, combined W2-0 behavior and contraction-fast identity remain unverified here.

(h) No new emitted route or IR rewrite outside eligible rank-3 strict stored-pool walks with threads found; all scatter users receive the changed shared header (including serial scatter3's leaf3 refactor), and the audit planner runs globally; LLVM/interpreter execution code is unchanged.

---

## codex-diff-W2-1

_formerly `bench/notes/round3/reviews/codex-diff-W2-1.md`_

VERDICT: DO NOT MERGE

Reviewed `667250cd..0bc965eb`; source/git inspection only. No builds, tests, emission commands or executions. Reproducers below are source-derived, not executed. `git diff --check` is clean. File:line references are at `0bc965eb` unless qualified.

1. **BLOCKER — the unchanged interpreter violates the newly documented contract for a compound set and a writing target.** `src/Interp/Loops.fs:2491-2498` calls `compoundToDense` BEFORE evaluating the target. That helper (`src/Interp/ArrayOps.fs:1713-1719`) reads every compact cell and constructs a fresh store. C++ now binds the set by reference, evaluates the target, then reads the original buffer (`src/CodeGenExpr.fs:572-615`); LLVM likewise obtains the pool, evaluates the target, then scans (`src/EmitLlvm.fs:3020-3071`). Thus this legal mutation shape, already supported by `index-types/520`, gives interpreter `false` versus compiled `true`:

   ```blade
   let ids = [1, 2, 3]
   let keep = mask(ids, lambda(v) -> v > 0)
   let mut B = compound(ids, keep)
   function replace(a: mut Array<Int64 like CompoundIdx<keep>>) -> Int64 = {
       a(0) = 9
       9
   }
   let hit = contains(B, replace(B))
   ```

   Hoist admission cannot repair this: it is an ordinary scan. **Fix:** in interpreter `IRContains`, retain the evaluated compound descriptor, evaluate the target once, then compare its compact cells directly; do not snapshot through `compoundToDense`. Audit the analogous `sparseToDense` branch. Add a three-lane pin for this mutation, including a target that mutates a different kept cell and misses/hits. Under the stated target-before-cell-read contract, `hit` must be `true`. The old scalar-counter pin never observes this snapshot.

2. **HIGH — “stored” is not proved through aliases; C++ can build before the set exists.** `src/Optimize.fs:4357` only tests `isDeferredForm sb.Value`. That predicate (`:912-924`) returns false for `IRVar`; `keyClass` (`:4229-4254`) also accepts `ArrayElem` without checking `IsVirtual`. Meanwhile `src/CodeGenBinding.fs:6126-6129` deliberately propagates deferral through an alias and emits only a comment. Example:

   ```blade
   type Three = Idx<3>
   let lazy = method_for(range<Three>) <@> lambda(i) -> Int64(i) + 1
   let B = lazy
   let src = [1, 4, 3]
   let flags = method_for(src) <@> lambda(v) -> contains(B, v) |> compute
   ```

   The planner admits `B` and `flags`. `memberHoistBracket` emits a read of `B.data` before all host code (`src/CodeGen.fs:1978-1988,2201-2205`), but `genComputeBinding` materializes the deferred capture inside that host's prelude (`src/CodeGenBinding.fs:1307-1309`). This creates a use before declaration/materialization; gate-off retains the existing capture-force path. LLVM's pool guard silently skips the build instead (`src/EmitLlvm.fs:8168-8186`), despite an applied planner decision. This differs from the reported pre-existing unforced-mask defect: use a compute host, which already forces captures.

   **Fix:** establish storage positively at the build point, chasing alias chains and virtual forms; decline deferred aliases in v1. Add a fail-closed C++ emission guard against deferred/streamed/unavailable set storage, with a truthful decline record. Pin direct and multi-hop aliases with the gate on/off and both lanes; require no build before materialization. Do not fix it by moving an unproved producer earlier.

3. **HIGH — runtime representation changes are unrecorded.** `src/cpp/blade_member.h:154-194` silently changes bitmap to hash after `calloc` failure, and hash to linear after allocation/capacity failure. `memberChooseRep` and the literal plan record (`src/Optimize.fs:4069-4082,4272-4292`) report only the preferred representation. `blade_member_kind_i64` (`src/cpp/blade_member.h:231`) has no production reporting caller. Equal `(M,min,max,knob)` therefore need not give equal *actual* representations; the Boolean contract survives, but the plan/measurement claim does not. The CLI driver tests successful allocations, not these branches.

   **Fix:** label the plan's result as preferred, explicitly describe the allocation fallback, and record actual kind plus fallback reason after each build in both lanes' runtime/run-record diagnostics. Add deterministic allocator-failure injection tests for bitmap failure and either hash allocation failing, checking equality, cleanup, and the reported route. Keep the probe read-only; report from the build/caller.

4. **HIGH — several central pins are route-less.** `tests/LlvmTests.fs:1225-1240` accepts any successful LLVM emission, including an entirely linear scan. The C++ build/probe/free needles (`tests/OptimizeTests.fs:3699-3749`) cover one dense mask fixture. Corpus 014/017/019/020 and the CLI multi-host output comparison can pass if their hoists disappear. In particular, 018 places membership in the MAP kernel of a fold, not the fold kernel advertised in the coverage summary. Compound 020 does not demonstrate a positions-free set: `positionsDemand` requires positions for an uncertified `IRContains` operand and for a capture (`src/Optimize.fs:3373-3376,3425-3426`), and its inline mask is not the named-mask fusion fixture.

   **Fix:** pair the actual corpus fixtures with per-host C++ and LLVM emission assertions: build before host, probe inside host, free afterward; negative cases require no build/probe. Cover empty compound, failing target, OMP map, a genuine fold-kernel probe, and compound cardinality/data access. For positions-free coverage, either add a narrowly justified census admission plus a joint-plan fixture asserting positions elision AND membership use, or explicitly record that this combination is currently unreachable and stop claiming it is tested. Add the alias and mutation fixtures from findings 1–2.

Contract/history check (a/g): formalism §6.5 (`docs/formalism.md:1478-1485`) delegates relational semantics to `docs/features/sql.md`; the new paragraph at `sql.md:232` records the once-target decision. There is no new paragraph in formalism itself, but this is not merely a pin flip. The baseline specification did not explicitly settle multiplicity/order; the builder brief selects the interpreter's once-target behavior, making the old C++ per-cell evaluation the defect being repaired. Files 015/016 are NEW in this diff, not repinned pre-existing tests. Baseline corpus/examples/stdlib searches found no effectful/failing targets: existing targets are literals or simple variables. Thus the supplied baseline 27/false observations concern new probes, not an old harness assertion of those values. Finding 1 qualifies the claim that the interpreter already implements the whole chosen contract.

Hook verification (five hooks):

| Hook | Exact locations | Static result |
|---|---|---|
| Target evaluation / old unwired path | `src/CodeGenExpr.fs:572`; `src/Interp/Loops.fs:2487`; `src/CodeGenExpr.fs:2249`; `src/CodeGenLoopNest.fs:37` | C++ set then target before scan; interpreter compound copy is the exception. Mask still calls by name; new routing is the side table/global, not SubstMap. The “any contains … linear scan” comment at CodeGenExpr:2251 is now stale. |
| Admission and registration | `src/Optimize.fs:4121,4371-4427`; `src/Lowering.fs:3193` | Shared write predicate covers producer payload, target-containing kernels, folds/seeds, complete host payload, and callback candidates; no interval between build and host. Storage proof fails for aliases (2). Mutable capture reads alone do not invalidate a stored-set lookup; writes decline. |
| C++ placement / lifetime | `src/CodeGen.fs:1963,2197,2315,3856`; `src/cpp/blade_member.h:123` | Build precedes host/team; free follows its completion and zeroes kind. Outside that window the probe scans. No positions access: rank-1 compound uses data/cardinality. |
| Shared chooser / shim | `src/cpp/blade_member.h:102,131,204`; `src/cpp/blade_llvm_shim.c:543`; `src/Build.fs:1268` | Unsigned differences/bit offsets handle negatives and extremes; duplicates and independent occupancy are sound. Checked capacity/byte bounds fall back. Header is deployed and hashed. Actual allocation fallback is unrecorded (3). |
| LLVM lowering / lifetime | `src/EmitLlvm.fs:2999,329,8167,8196`; `src/cpp/blade_llvm_shim.c:82` | Real scan/probe arm; `_has` only reads, so `memory(read)` fits. Normal completion finalizes. Panic exits the process, bypassing fini: exit-time leak, no later dangling probe. |

Composition/knob check (d/f): membership planning follows compaction planning. A directly deferred host declines; an inline compound mask is bracketed at the compound. Current compaction `totalScalar` refuses contains/captures (`src/Optimize.fs:3256-3269,3486`), so a membership predicate does not secretly migrate into the earlier fused mask pass. The gate is registered (`:167`), cleared/restored through `suiteClearedKnobs` (`src/CliSelfTests.fs:96`), and forced values appear in plan evidence. `CLAUDE.md:205` describes those settings and fallback, but “deterministically” applies only to the preferred chooser, not the actual allocation-dependent representation.

(i) No contains-free C++/.ll emission change identified by static diff inspection: empty plan tables add no text and new state cells do not consume IDs; LLVM shim/header contents and cache identity DO change globally. Byte identity was not executed or measured.

---

## codex-diff-W2-1-2

_formerly `bench/notes/round3/reviews/codex-diff-W2-1-2.md`_

VERDICT: MERGE AFTER FIXES

Reviewed 0bc965eb..0a37e6c4, with relevant 667250cd..0a37e6c4 context and W2-3's 8f2d2480 header diff. Files/git only; no builds, tests, Blade or g++ execution. git diff --check is clean. Locations are at 0a37e6c4 unless qualified. No new BLOCKER demonstrated.

Original 1 — RESOLVED: IRContains retains dense/compound/sparse storage, evaluates the target once before either traversal, and evaluates it even at cardinality zero (Interp/Loops.fs:2496-2521); the first review's claimed compound false/true reproducer was incorrect because interpreter assignment refuses that store, rather than returning false.
Original 2 — RESOLVED: storageOf positively proves earlier top-level storage through IRVar chains, conservatively declines other alias forms, audits every accepted chain binding, and both emitters decline unavailable storage explicitly; the original ordinary-let alias reproducer actually forces and copies at its alias declaration.
Original 3 — RESOLVED: preferred/actual/fallback are distinct, reports come from callers, and deterministic allocation hooks exercise bitmap/hash failures and cleanup; capacity overflow remains explicitly untested. The run-record integration qualification is NEW 3 below.
Original 4 — PARTIAL: 27 shared rows cover today's intended files, including a real fold-kernel probe and explicit LLVM refusals, but the C++ assertions do not prove that the actual host executes inside the build/free window (NEW 1); error acceptance is too broad (NEW 2).

1. NEW HIGH — C++ host placement remains incompletely route-pinned. tests/OptimizeTests.fs:3785-3790 verifies main < build < report < free, but `hostCode` accepts ANY non-comment line; a probe in any function before main automatically satisfies the location check. It never locates that function's host call or the host's loop. Moving a build/report/free window around unrelated executable code while leaving the host call outside it can still pass; the unbuilt-global fallback scan preserves value pins. Production placement is currently correct at src/CodeGen.fs:2226-2230, but the requested regression assertion is not established. Exact fix: carry the expected host identity in each row (or derive it from the lowered plan), locate its actual call/loop and completion, and require build/report before that region and free after it; for out-of-main probes, associate the containing callable with an invocation inside that region. Assert corpus/table name-set equality too: the current table-driven loops detect removed/renamed rows, not new unlisted files.

2. NEW MEDIUM — Refusal/guard tests can pass on unrelated failures. tests/LlvmTests.fs:1276-1284 folds lowering, IR validation and LLVM emission failures into the same Error; both `emits=false` rows accept any such error. tests/OptimizeTests.fs:3833-3838 turns an emitter exception into text and passes if a decline was recorded first; :3850-3852 similarly erases the distinction between emitted IR and refusal. Exact fix: fail on lowering/validation errors and exceptions; store and match the expected LLVM refusal reason per negative row. Require successful C++ emission plus the fallback scan for the injected deferred capture. For LLVM, explicitly assert either successful fallback-scan emission or the known unsupported-computed-set refusal, alongside the build-point decline and absence of init; do not label an arbitrary error a successful guard.

3. NEW MEDIUM — Integration changes the run-record format for unrelated programs, and the membership JSON path has no new record pin. W2-1 alone preserves no-build records: src/cpp/blade_run_record.hpp:205-208 writes the same `]}\n` when extra_members is null. W2-3 at 8f2d2480:src/cpp/blade_run_record.hpp:221 unconditionally inserts `"forms":[]`, including programs with neither contains nor a runtime form event, while the record version remains 1 (:189). This is a W2-3/integration scope finding, not a W2-1 comma bug. The existing tests/RunRecordTests.fs:280-289 check substrings and do not reject that format expansion or exercise member_hoist. Exact fix: emit forms/forms_dropped only when there is a recorded event/drop, preserving unrelated records; add parsed-JSON fixtures for no events, membership only, forms only, and both together, asserting the actual route fields. The orchestrator must run the run-record block on the merged header; the reported component footers do not cover it.

Contract/storage checks:

- All three interpreter set kinds are set-first, target-once, cells-after. There is no empty-set return before the target; a BL8013 target therefore still fires. compoundCompactCell/sparseCompactCell read the original Data store (:1713/:1878), not a dense copy. Corpus 016 pins the empty compound's failing target; 023 pins dense mutations. The compound mutation is deliberately not a three-lane pin: src/Interp/Core.fs:1239-1248 accepts only VArray for indexed assignment, so VCompound and VSparse are unsupported. LLVM's static parameter shape rejects compound axes (src/EmitLlvm.fs:1568,7456-7462); 024 falls back to C++. Accept the named G65 follow-up, “interpreter's compound element store,” plus the documented LLVM compound-parameter limitation (docs/features/sql.md:242-246; tests/InterpDiff.fs:63-70). This is an existing support gap, not a newly demonstrated differing Boolean or wrong panic order in a supported lane.
- storageOf does NOT follow every alias form. It follows earlier module IRVar chains (:4192-4200); IRLet/block results and IRProject/tuple projections fall through its refusal (:4206), while local and parameter set IDs fail the top-level gate (:4394). A function-body host is outside v1; a materialized IRApp result is separately admitted. These are conservative scope exclusions, not unsafe admissions. Mutable/assigned/provider-write and producer-payload checks cover every admitted chain (:4403-4428).
- Ordinary `let B = lazy` has IsMutable=true (TypeEnv.fs:1741; TypeCheckInfer.fs:16761); CodeGenBinding.fs:6122-6125 forces lazy, then :6240-6245 deep-copies B. The :6126-6129 deferred-alias arm applies when that assignability guard is false. The earlier review omitted it. A directly captured deferred map is forced in the host prelude (:1307-1309), after the hoist build point. The planner now declines both shapes first; the emitters' storage guards are defensive backstops exercised by injected plans, not normal routes for those fixtures. Their comments claiming alias deferral without this qualification are overbroad.
- The 27 rows cover 20 contains/member files in sql-unique-contains plus all seven sql-semijoins files. Nine C++ rows assert only absence of member text: unique-contains 004-006,015-016,021,023-024 and semijoins 005. Eight also assert no LLVM member text; 024 asserts refusal instead. Those negative checks alone pass with hoisting globally off, as expected; the 18 positive C++ rows and 17 positive LLVM rows prevent the whole table passing that way. LLVM's successful rows check init < report < every probe < fini by slot. 025 probes in the fold kernel itself. G65 now explicitly disclaims positions-elided membership coverage.

Hook verification (five):

| Claim | Exact hooks | Static result |
|---|---|---|
| Live interpreter cells after target | src/Interp/Loops.fs:2496,2500,2520; src/Interp/ArrayOps.fs:1713,1878; src/Interp/Core.fs:1245 | All three representations retain storage; empty sets still evaluate target; compound/sparse element stores are unsupported. |
| Storage proof through aliases | src/Optimize.fs:4179,4192,4394,4403,4425; src/CodeGenBinding.fs:6122,6240 | Earlier module aliases are traced and audited; unsupported forms decline; ordinary dense aliases force/copy before the host. |
| C++ fail-closed build/report/lifetime | src/CodeGen.fs:1975-1987,2002-2012,2226-2230 | Availability checked before emission; report immediately follows build; host follows; free follows host. Test weakness is NEW 1. |
| LLVM truthful decline and report | src/EmitLlvm.fs:8174-8204,8210-8217; src/cpp/blade_llvm_shim.c:561 | Only rank-1 Int64 APool builds; rejected plans record llvm-lane decline; caller reports, then host/probes, then fini. |
| Allocation fallback and JSON hook | src/CliSelfTests.fs:1121-1126,1192-1208,1236-1256; src/cpp/blade_member.h:314,329,340; src/cpp/blade_run_record.hpp:111,207 | F1-F6 use allocation ordinal masks, not timing; check booleans, live allocations and route text. Probe has no reporting side effects. extra_members is a generic single callback, not a multi-subscriber registry. W2-3 writes forms directly, so no callback overwrite: merged JSON is `...,"forms":[...],"inputs":[...],"member_hoist":[...],"member_hoist_dropped":0}`. First 64 membership reports retained; later ones counted as dropped. |

(5) W2-1 fix: no contains-free emitted C++/.ll text or no-build run-record format change identified (empty plan guards; demand-driven shim declaration); runtime header/shim contents and cache identity change globally. With W2-3 merged, every record gains forms even without events: NEW 3. Static inspection only.

---

## codex-diff-W2-2

_formerly `bench/notes/round3/reviews/codex-diff-W2-2.md`_

VERDICT: MERGE AFTER FIXES

Read-only review of e2194721..278353cb; source references below are at 278353cb unless stated. Consulted integration tip 58efa62d and the W1-4 records stored there as codex-diff-W1-T4*.md. No builds, tests, compiler emission, Blade execution or g++ run. Saved generated artifacts are builder evidence, not independently reproduced results. git diff --check is clean.

1. **BLOCKER — single-compound emission is changed, contrary to this review's explicit identity requirement.** Optimize.fs:3698-3704 excludes only a singleton whose dense source IS the mask source; a singleton over another stored column enters SharedSelection. Independently, :3640-3653 / :3917-3943 enable the new membership certificate for the old single-compound path. Thus both `compound(amount, keep)` with an ordinary total predicate and `compound(cust, keep)` with an admitted contains predicate change emission with only ONE compound. The latter formerly failed totalScalar and now counts/fuses. tests/OptimizeTests.fs:1580 deliberately supplies a singleton semiagg case.

   This is intentional Part B/brief functionality, not a demonstrated wrong-value result; it nevertheless meets the user's stated BLOCKER definition. **Exact fix:** isolate singleton/new-certificate expansion from this identity-preserving change: require at least two companions before taking either new admission, and deliver singleton Part B separately (or explicitly revise the singleton-identity requirement before accepting that expansion). Pin baseline/default emitted text for legacy singleton shapes, including contains. Do not claim blanket singleton identity from the helper refactor.

2. **HIGH — the producer party does not prove storage through aliases, and omits x when x is not a companion source.** Optimize.fs:3728 tests only `isDeferredForm cb.Value`; IRVar aliases pass. :3736/:3753 audit only c_j, not the mask source srcId. The code claims all producers are stored/never assigned without establishing those facts. C++ genSharedCompanion (:2727-2733) accepts a name in VarNames as a buffer, then emits `pool_base(alias.data)`. But genVarAliasBinding (:6445-6453) puts a deferred alias into VarNames while emitting only a COMMENT. Only the CURRENT compound's operand is forced (:2442), not future companions' operands. The mask-site arm likewise trusts names (CodeGenExpr.fs:2299-2306).

   Source-derived probe (not executed; use --print n to avoid the unrelated deferred-alias auto-print defect):
   ```blade
   type I = Idx<3>
   let x: Array<Float64 like I> = [1.0, 2.0, 3.0]
   let lazy_col = method_for(x) <@> lambda(v) -> v + 10.0
   let alias_col = lazy_col
   let keep = mask(x, lambda(v) -> v > 0.0)
   let y1 = compound(x, keep)
   let y2 = compound(alias_col, keep)
   let n = extents(y2)
   ```
   Predicted default C++: T4c admits the alias and reads an undeclared alias_col at y1; the ordinary unshared path forces it at y2. Under gather the undeclared read moves to keep. LLVM's pool checks can decline instead, creating a lane discrepancy.

   **Exact fix:** factor/reuse membership's positive storage proof (Optimize.fs:4768-4795), including alias chains, virtual/streamed sources and supported stored roots, for srcId plus every c_j. Apply assignment and payload-write checks to the relevant chains. Also fail closed in C++ when any source remains in DeferredComputations; do not force a future companion early, since its producer may panic. Add alias-chain, streamed-source and x-not-in-companions decision/emission probes, with gate-off value/failure twins.

3. **HIGH — runtime/shared-route coverage is overstated; 542 is a route-less T4c pin.** The saved builder artifact `generated_cpp_tests/opt_diff/0401_Compaction_fusion_T4c_two_companions_over_a_run-time_source_extent_on.cpp` (11:11:39, before this commit) emits separate counting constructors at lines 263 and 272 and separate gathers at 267/276. There is no shared selection, mask count handshake or runtime shared gather guard. Its value pins therefore do not test what its comments promise, even in C++; LLVM's runtime-range refusal is an additional limitation.

   CliSelfTests.fs:926-956 supplies a different dyn2 fixture, but checks outputs only and explicitly disables LLVM. The static shared fixture's sel_cap=0 chooses the fallback at planning time, not the LLVM runtime false edge. The LLVM emission test (OptimizeTests.fs:1666-1683) checks comments plus an aggregate allocation-count difference; it does not prove distinct companion pool wiring or either runtime guard edge. Ownership checks similarly count comments, not an executed free or a live registration.

   **Exact fix:** replace/adjust 542 to a shape positively admitted by T4c and assert its plan plus actual shared buffer/index wiring. Add route assertions to dyn2, including the runtime cap comparison and guarded joint fallback. Supply a supported LLVM dynamic-source fixture exercising BOTH guard edges, with positions demanded and elided and strict chains on multiple companions; otherwise decline that unverified runtime shared-gather arm and record it unbuilt. Assert distinct destination allocations and each companion's binding to its own destination, one shared position allocation, and no per-companion index deletion. Label lifetime checks as program-lifetime/no-free checks, not executed single-free coverage.

Audit of the remaining attack points:

- **Ownership / partial plans:** Lowering.fs:904-909 refuses nested/block/function compound expressions; only top-level TDeclLet is intercepted (:2475-2494). REPL/notebook lowering uses the same lowerTypedProgram (Interp/Repl.fs:103); interpreted cells do not execute C++ teardown. registerShapedAlloc is a NO-OP without a scope (CodeGenLoopNest.fs:4725-4728), so currently there are zero actual top-level registrations/frees, despite the ownership comment. deallocate_compound_shared_index deletes only data (nested_array_types.hpp:347); index-only deletion is separate (nested_array_utilities.hpp:733). Group admission is all-or-nothing (:3873-3889), so a planner-declined last companion cannot leave earlier shared plans orphaned. No currently reachable double-free/dangling-index path established. This does not prove a future local/escaping-compound lifetime policy.
- **Failure order / census:** group intervalPayload checks (:3792-3803) and B4 fold-host/interval checks (:4020/:4039-4041) cover the moved pure stored-data work, subject to finding 2. BL8003 remains at the fold in CodeGenExpr.fs:1397-1404 and EmitLlvm.fs:6550-6565. Early buffers are not automatically printed on the failure path: C++ printCode follows the body (CodeGen.fs:3625); LLVM also prints after execution. Run records do not serialize those future buffers. No moved BL8003/BL8006 established. positionsDemand runs separately for EVERY companion and unions failures (:3813-3815); aliases, arguments, captures, side operands, types and foreign references conservatively retain positions. The inherited W1-4 embedded-index traversal caveat is not repaired by this commit; no new reachable escape established.
- **Certificate / order / mirrors:** the proof consumes the admitted host+kernel+set plan, checks targets with unchanged totalScalar and restricts captures (:3596-3637). T1's storage/alias and host-payload effect proofs remain the proof input. Moving its planner earlier introduces no CompactionTable dependency: deferred-host refusal uses hostExprs/isDeferredForm (:4932-4946), not compaction plans. Inline-mask hosts already existed in T1; T4c still requires a let-bound mask. b4/unshared bypass the certificate, but DO NOT disable membership hoisting: a T1-admitted contains still probes the hoisted structure. “Those mirrors imply per-probe scan” is false; scan requires T1 refusal/off.
- **Identity / cap:** the C++ single-buffer helper maps each feed to the same compact buffer and preserves its strings/order (CodeGenExprSupport.fs:2638-2660); the LLVM single-pool adaptation likewise preserves square/feed ordering (:4380-4453). ByMask first-wins is inert for the predecessor's at-most-one-plan admission. The cap sums every companion width plus one 8-byte position cell, then uses n < floor(capBytes/perCell) (:3821-3844), correctly charging n+1 per buffer. Failure retains T4c's joint m+1 pass at the first companion, not unshared; that fallback is recorded.
- **Pins / skips:** 532/533 exercise keyed companions; 534 is a type rejection; 535/536 declines; 537/539 certified semi/antijoins; 538 uncertified; 540 first failure; 541 J=2 gather via opt-diff's mirror. The source assertions cover these categories but do not discharge finding 3. All 515-531 have saved on/off C++ AND executable pairs (opt_diff ordinals 0374-0390), including their non-inert mirrors; none is established as skipped by the supplied 136 aggregate. OptDiff counts identical emission as PASS, not SKIP (tests/OptDiff.fs:165-173). Skips mean dual refusal or unavailable/failed compilation; the exact 136-name census cannot be recovered from the footer alone. No fresh execution verification claimed.

Five hook checks:

| Claim | Hook at 278353cb | Result |
|---|---|---|
| First plan owns ByMask; complete shared group | src/Optimize.fs:3165; :3873 | First entry retained; shared plans constructed together. |
| Every companion contributes position demand | src/Optimize.fs:3813 | Union of per-companion fail-safe censuses. |
| Joint C++ pass, separate buffers, one index | src/CodeGenBinding.fs:2696; :2749; :2800 | Present; name-only storage assumption is finding 2; registration no-op at top level. |
| LLVM mask gather and shared adoption | src/EmitLlvm.fs:4550; :8191 | Present; dynamic false-edge validation absent, finding 3. |
| Certificate consumes T1 admission before T4 planning | src/Optimize.fs:3596; src/Lowering.fs:3182 | Present; singleton expansion is finding 1; T1 host refusal remains IR-based. |

(i) YES: singleton different-column compounds and singleton certified-contains compounds change emission; no independent singleton change found in first-wins or the single-buffer helper refactors, and no membership decision change found solely from planner reordering.

---

## codex-diff-W2-6

_formerly `bench/notes/round3/reviews/codex-diff-W2-6.md`_

VERDICT: MERGE AFTER FIXES

Reviewed 844e2ae7..46dec984. Locations below are at 46dec984. Files/git only; no builds, tests, Blade or compiler execution. git diff --check is clean. No default allocator-policy change, default generated-source change, or production translation-unit policy split found.

1. HIGH — The LLVM footprint is misrecorded. src/CliCommands.fs:1216 unconditionally writes `budget_scope:"per-thread"`, even with `lane:"llvm"`; src/Toolchain.fs:284-288 also leads with per-thread wording (the arm's trailing LLVM qualification does not repair JSON, and the default has none). The actual LLVM cache is process-global (src/cpp/blade_llvm_shim.c:295-299). bench/README.md:78-91 says BOTH lanes, then attributes a per-thread budget, thread-exit lifetime and run-record evidence to the arm without the LLVM exceptions. G52 and CLAUDE.md:222 disclose those exceptions, but the benchmark protocol and machine-readable plan contradict them. Exact fix: derive text/JSON scope from the selected lane (`process` for LLVM; `per-thread` for C++, including LLVM refusal/fallback); pin both lanes and fallback. Amend README: C++ may retain T x budget until reuse/eviction/flush/thread exit; LLVM has one process budget and atexit flush, and has NO run record. Its plan plus shim identity is acceptable evidence for this scoped arm if captured beside the measurement and described as build evidence, not an observed runtime record. Preserve the existing peak-can-rise-without-reuse and relocation-is-not-less-work qualifications.

2. MEDIUM — Spoiled numbers are still in the permanent record. bench/notes/gap-register.md:102 prints the contaminated 52.8 -> 30.7 / 28.5 -> 46.0 ms pairs and their range, then claims the wave-3 probe is the only timing on record. Labeling numbers “NOT A MEASUREMENT” does not keep them out. The same paragraph promotes that loaded batch's tiny peak-working-set differences as “the only stable number.” Exact fix: replace the entire quantitative W2-6 directional paragraph with “Attempt spoiled by concurrent CPU load; no performance conclusion; quiet harness A/B pending.” Keep only the previously accepted wave-3 probe numbers, explicitly probe-only. Do not infer footprint stability from the spoiled batch.

3. MEDIUM — The new tests undo the suite's toolchain-file protection. tests/RunRecordTests.fs:357-360,383,407 unset the variable for their default emission/record cases; tests/LlvmTests.fs:3092-3094 do likewise for the default shim identity. With a supported persisted `BLADE_POOL_RETAIN=1`, getWithOrigin (src/Toolchain.fs:79-86) then selects the file's arm. Default/no-define assertions fail and the alleged default/arm comparison can use the same policy. suitePinnedKnobs at src/CliSelfTests.fs:109 is correct; these scopes bypass it. Exact fix: use explicit `Some "off"` for operational default cases. Test unset behavior separately against an isolated empty toolchain fixture; add file=1/env=off and file=off/env=1 precedence cases, restoring the file selector and Toolchain cache afterward. The pure parser's None cases need no change.

4. MEDIUM — Default run records DO change; make the compatibility contract explicit. src/cpp/blade_run_record.hpp:193 retains version 1, while :219-233 adds policy.pool to every generated C++ record, including scalar/default programs. This is a universal build-policy field, unlike W2-3's empty event list in W2-1 second-look NEW 3: recording default policy is meaningful and is explicitly requested by the builder contract. It is therefore an intentional format extension, not evidence of a silent retention default change. tests/RunRecordTests.fs:407-419 pins the two new substrings, but does not parse the combined document or pin version/compatibility. Exact fix: document policy.pool as an additive, optional v1 field (old v1 records may lack it), and add parsed-JSON fixtures/assertions for default, explicit pair and cache-off records, including coexistence with forms/member fields. A version bump is unnecessary for that documented additive contract; do not claim old/new default records are byte-identical. This is the explicit exception needed to (h), not a reason to hide the default policy.

5. LOW — Default source noise leaks into every suite plan. src/Toolchain.fs:282 adds `-- BLADE_POOL_RETAIN=0 (env BLADE_POOL_RETAIN)` under the suite's off pin. Current plan needles use Contains (src/CliSelfTests.fs:531-544), so they tolerate rather than prevent it. Exact fix: omit the source suffix for a valid default setting; show provenance for non-default policies and retain malformed-value diagnostics. Pin equality of the ordinary unset/off default build line using an isolated toolchain fixture.

Hook verification (five claims):

| Claim | Exact hooks | Static result |
|---|---|---|
| Default caps/types and lifetime | src/cpp/nested_array_utilities.hpp:120-133,159,175,209; src/cpp/blade_llvm_shim.c:237-253,295-311,407-413 | C++ fallback is exactly `constexpr size_t` with `size_t(4) << 20` / `size_t(8) << 20`; LLVM keeps its original size_t casts. Pair-presence guards and max<=budget checks exist. C++ thread-local reaper; LLVM 16-slot process cache, oldest eviction, atexit flush. |
| C++ policy propagation and cache | src/CodeGenLoopNest.fs:3827-3840,3858,3868; src/CodeGen.fs:3752-3766,3867-3896; src/Build.fs:722-738 | Only non-default policies prepend comment/defines; runtime precedes record header. Self-contained/multimodule generation uses this same path; MPI adds includes afterward. Cache includes emitted text AND runtime-header digest (and normalized flags), not just text/CPU. |
| LLVM defines enter both identities | src/Build.fs:1246-1253,1276-1284,1363-1375; src/cpp/blade_llvm_shim.c:237-250 | Same byte-valued pair reaches shim flags, then shim hash, then executable key; default flags add nothing. Shim source changes invalidate even default cached objects. No LLVM runtime run record is implemented. |
| Parse, precedence, persistence, isolation | src/Toolchain.fs:79-86,223-225,240-272,290-298; src/Setup.fs:274-296; src/CliSelfTests.fs:95-109 | Env wins; bare setup persists it. Positive ordered Int64 pair bounded at 1048576 MiB shifts to at most 2^40 bytes: safe for the supported 64-bit size_t targets. Zero/negative/non-numeric/overflow reject; trimmed case-insensitive true/false match the shared switch grammar. Warning deduplicates per trimmed malformed value. Off emits no defines. Test hole: finding 3. |
| Record markers and alternate emitters | src/cpp/nested_array_utilities.hpp:126,130,226; src/cpp/blade_run_record.hpp:219-231; src/CodeGen.fs:3642-3673; src/Build.fs:411-416; src/CliCommands.fs:308,737-748 | kRetainArm selects arm/default in the record (also tested); POLICY_DECLARED prevents references when standalone consumers include the record without the pool header. Neither is dead. REPL/notebook compiled fallback/render use compileToExe and shared generation. CUDA kernel/cuBLAS device TUs omit the defines but do not include/use blade_pool; no split found. |

Other paths: memcheck keeps the arm's cap metadata but disables caching through Build.fs:523,545,562 / LLVM :1253; header ASan detection remains. Standalone hand-written C++ probes bypass generation and stay default unless given defines (AllocTests.fs:21-30 supplies them explicitly); they are separate executables, not conflicting TUs. Managed interpreter/oracles do not acquire this allocator policy. No bypassing production C++ fallback emitter found.

(h) Default generated .cpp/.ll text and allocator policy are unchanged by static inspection; default C++ run records gain policy.pool (finding 4), and runtime/shim identities change; the four reported source comparisons do not cover CUDA companion/cuBLAS TUs, MPI, memcheck, REPL/notebook fallbacks or standalone probes, nor runtime JSON.

---

## codex-diff-W2-4

_formerly `bench/notes/round3/reviews/codex-diff-W2-4.md`_

VERDICT: MERGE AFTER FIXES

Read-only review of e4dec0ba..c0b927a3. Locations below are at c0b927a3. No builds, tests, execution, or measurements performed; reported footers are not independently verified. git diff --check is clean.

1. **HIGH — dominance is not transitive through seed callables.** `src/Optimize.fs:2833` examines only the immediate kernel body/captures; `:2843` uses `collectVarRefsIR` on the seed. That walker does not enter resolved callables (`src/IR.fs:2664`). Yet `sfTotal` accepts calls and recursively audits their bodies (`Optimize.fs:5406`). Witness: a total nested seed function capturing `c`, with `c` bound between the producer and the join; both folds use `seed_fn(0.0)`. Nested self-bound functions have no region IRLet (`Lowering.fs:1317`), so the seed references a function id absent from `insideIds`, not `c`. Sibling grouping's transitive availability check accepts this because `c` precedes the JOIN; the new admission then incorrectly claims it precedes the PRODUCER. Repro C++ and LLVM evaluate that seed before `c` exists. The same incomplete proof applies to indirect kernel captures. This is a missing dominance proof, not a demonstrated runtime result in this read-only review.

   **Fix:** use the existing cycle-safe `freeVarsOf` closure (`Optimize.fs:5747`) for BOTH kernels and seeds, or expose its equivalent through `EpilogueLegFacts`; resolve callees and their captures transitively before checking availability at the producer. Add a late captured seed-function case and an indirect kernel-capture case, plus dominating positive counterparts; assert refusal/admission and both backends' route.

2. **HIGH — the C++ A/B remains confounded; the attribution is stronger than its evidence.** G69 (`bench/notes/gap-register.md:117`) DOES disclose the moved free. However, `CLAUDE.md:205`, `docs/features.md:281`, and the inline decision (`Optimize.fs:6234`) omit the retained-local lifetime/working-set change. Gate-off restores the call and its scope-exit free; gate-on retains all callee locals in the caller's block. The prospective r3w2/r3w2legs0 comparison therefore measures fusion plus inlining/lifetime effects. One hand-patched comparison, 15.84 versus 15.33 ms, supports a directional estimate, not the causal decomposition “0.5 ms free / 1.6 ms fusion.” LLVM's absence of this free confounder does not prove that all its gain comes from fusion rather than other consequences of IR inlining.

   **Fix:** preferably retain the callee's cleanup boundary at the original return point while exporting the scalar result and leg state. Otherwise add this exact plan/docs wording: **“Inlining extends callee-local pool lifetimes to the enclosing caller block; scope-exit frees may move outside the timed region. EPILOGUE_LEGS on/off measures fusion plus inlining/lifetime effects, not fusion alone.”** For saxpy append **“y2: 16,000,024 payload bytes retained past kernel return.”** Label the 0.51 ms as a single probe estimate and remove the residual causal attribution until an interleaved, lifetime-matched control is recorded. Cross-reference G52: delayed free changes live storage and can interact with retention/reuse; the two gains must not be added independently. Record peak working set in the harness.

3. **HIGH — the mandatory failure fixture is route-less.** `tests/corpus/sibling-folds/026_input_legs_failure_between_aborts.blade:19` pins BL8013 and a source line only. No new OptimizeTests case asserts that THIS program admits and emits A7c. It passes unchanged if A7c always declines. It also does not compare the full diagnostic/frame with gate-off or establish prior stores. The generic positive C++ needles (`OptimizeTests.fs:1800`) and LLVM loop-count test (`:1853`) do not establish the failure fixture's route. The latter counts loops over the whole module, rather than inspecting the participating accumulator updates.

   **Fix:** add backend-specific emission/decision assertions on 026 itself: both input legs in the producer, no joined pass, gate-off restores that pass, producer stores precede the failing binding. Compare full fault/site/frame with gate-off in both lanes. Add a B1-shaped throwing CALLEE negative case to pin that the new retained-inline arm refuses it and keeps its frame. Strengthen the LLVM route check to identify the two input accumulators and their updates in the producer loop. The new LLVM cell-epilogue checks (`CellEpilogueTests.fs:558` onward) compare printed values, not raw bits; add raw-bit probes for the claimed identity contract.

4. **MEDIUM — the inline is committed before either emitter proves it can use the plan.** `Optimize.fs:6244`/`:6254` retain it on planning admission, while C++ and LLVM can subsequently decline (`CodeGenBinding.fs:416`, `EmitLlvm.fs:4687`). For example, a B1-shaped consumer with a nonliteral seed passes `epilogueReserved`'s structural check (`Optimize.fs:5689`), but repro C++ and LLVM require B1's seed to be literal. The call can therefore disappear and its pools live longer even though the separate input pass remains. No rollback restores the pre-A7c shape. This is broader than “keep the inline when the legs ride.”

   **Fix:** share the necessary B1/backend eligibility facts with the retained-inline decision, or preserve an original-call fallback until emission commits the fusion. Pin nonliteral B1 seeds and disabled flat/lane routes: when no A7c route is emitted, an inline retained solely for A7c must not change call/lifetime behavior. At minimum distinguish planner admission from emitted fusion and disclose the retained inline on every fallback.

5. **MEDIUM — correct the brief's licence premise and the resulting claims.** Saxpy's builtin `(+)` legs are NOT unlicensed merely because they lack `where omp`. `docs/formalism.md:225` explicitly licenses recognised builtin `+`/`*`; the pre-existing predicates are `CodeGenExprSupport.fs:1806` and `EmitLlvm.fs:1171`. `CodeGenCuda.fs:2237` consults each leg's own predicate. Thus saxpy is strict in repro and licensed in the default build; its C++ feed_sum/finish_sum tree differs from the old joined omp-simd tree legally. No strict-chain-laning violation established. Actual unlicensed subtraction legs take chains, including in a licensed build. LLVM still uses B1's own `foldFmfDecorable` result (`EmitLlvm.fs:4607`); adding loop recurrences can change the vectorizer's licensed reduction tree without changing B1's semantic form. The reported checksum last digit is compatible with that licence, not evidence of strict reassociation.

   **Fix:** amend the brief/report's “two STRICT legs (no source licence)” to “strict under repro; builtin-licensed otherwise.” Name the C++ association change in the emitted decision itself, not just comments/docs. Keep strict-leg bitwise checks separate from licensed tolerances; do not claim the new tree equals the old joined tree or guarantee 1e-12 for arbitrary cancellation-sensitive data.

Other attack checks: the retained inliner still rejects assignments, capturing callees, repro callees, and non-total bodies (`Optimize.fs:5533`); a panic inside an admitted kernel is therefore not an established frame-change counterexample. Parameter substitution uses fresh ids and caller operands; same-id/index/extent plus the write barriers supports that identity proof across an accepted call. Direct slice expressions are not inline arguments; a differently named alias fails operand identity. The code does not prove blanket “no views anywhere”: an identical named view supplied to both traversals is a different case and needs an explicit contract/test. Intervening statements use `intervalPayloadOf` over the new producer-to-join interval and member statements (`Optimize.fs:2856`). The callback extension shares the implementation (`:1516`, `:1524`, `:1527`), with the old predicate selecting callbacks=false; no divergent second write engine was introduced. Dominance remains finding 1.

The interpreter DOES execute the retained-inline IR, then the unchanged join; “interpreter unchanged” means implementation, not program shape. `test interp` cannot validate the inline itself. `BLADE_EPILOGUE_LEGS=0` suppresses this additional inline, and registration in optimizerGates supplies the opt-diff comparison. Output-leg exclusion and the repro/team decline have decision tests; the team test does not assert absence of a leg-specific omp census. Corpus 019-027 has no collision: e4dec0ba contains 001-018. Writing/non-total legs are rejected upstream, so those fixtures do not exercise the new admission's defensive rows.

Hook verification (five claims):

| Claim | Verified hook | Result |
|---|---|---|
| Shared admission beside B1 planner | `src/Optimize.fs:2754`; `planHotRowFolds:2902` | Present; transitive dominance incomplete. |
| Inline retained only for admitted A7c; gate registered | `src/Optimize.fs:6234`, `:6254`, `:175` | Planning admission, not backend success; IR changes. |
| C++ legs attached and fed inside producer | `src/CodeGenCuda.fs:2214`; `src/CodeGenLoopNest.fs:2584` | Strict cell/block chains or own licensed lane state. |
| Join binds published results, with ordinary fallback | `src/CodeGenBinding.fs:384`; `src/EmitLlvm.fs:4077` | Present; fallback does not undo inline. |
| LLVM flat-epilogue twin | `src/EmitLlvm.fs:4566`, `:4617` | Per-leg seeds/flags; one fused loop, published slots. |

(g) No unrelated emission change found for programs with no A7c candidate; the shared-write refactor and empty-Legs paths preserve their old behavior. Structurally admitted candidates whose backend later declines still change emission/lifetimes through the kept inline (finding 4).

---

## codex-diff-W2-4-2

_formerly `bench/notes/round3/reviews/codex-diff-W2-4-2.md`_

VERDICT: DO NOT MERGE

Read-only second look at 62ba1a49..9f2877a7, with whole-item context. Locations below are at 9f2877a7. Both blocking paths survive integration squash 58efa62d (Optimize locations there are +3 lines). No builds, tests, execution or measurements; witnesses below are source-derived, not executed. git diff --check is clean. Reported test/ASan/timing results are not independently verified.

Original findings:

1. RESOLVED — kernel AND seed dominance use cycle-safe FreeVarsOf (Optimize.fs:2876, :2877, :6463); 028 correctly distinguishes upstream refusals from positive admissions. The intervening audit uses complete intervalPayloadOf payloads and transitive Writes (:2894, :6881), not merely immediate references.
2. NOT RESOLVED — the cleanup renderer is shared, but the supposed return boundary is not proved; moving frees to an arbitrary join introduces live-storage frees and duplicate releases (NEW 1–2). The revised saxpy measurements and superseded-number wording do not establish the general lifetime claim.
3. RESOLVED — 026 itself has C++/LLVM route assertions (OptimizeTests.fs:1942), plus gate-on/off full fault text comparisons for 026/029 in both lanes (CellEpilogueTests.fs:608). LLVM value checks now require raw bits (:582). Limitations below.
4. PARTIAL — nonliteral B1 seeds and disabled licensed lanes now refuse before retaining the inline, and records distinguish planning from emission. The mirror remains incomplete. Keeping a disclosed pure-total inline after an emitter decline is acceptable once cleanup is correct; NEW 1–2 prevent that conclusion today.
5. RESOLVED — code decisions and repository docs say builtin-licensed outside repro, name the C++ association change, and restrict 1e-12 to tested fixtures. The external brief still has its old premise; G69 explicitly corrects it. Strict bits and licensed tolerances are separate.

NEW findings:

1. **BLOCKER — the join can be INSIDE the inlined callee, before its last pool use.** Optimize.fs:6920 takes the earliest fold member, :6930 requires only producer < join, and :6978 collects EVERY IRCompute let belonging to that inline. There is no check that all callee statements precede the join. The rewrite inserts the join before that earliest member (:7097); CodeGenBinding.fs:436–455 releases those pools before executing/binding the join. Scope escape is not last-use analysis: an ordinary later read does not put a pool into Escapes (CodeGenLoopNest.fs:4401).

   Witness shape: extend 019's scalar-returning kernel, after its existing `y2` and B1 `s`, with `a = reduce(x, (+))`, `b = reduce(y, (+))`, then `z = method_for(y2) <@> lambda(w) -> w + 1.0 |> compute`, followed by a B1 fold `q` of z; return `s + a + b + q`. Keep the caller's x/y input sums. In serial repro with BLADE_POOL_REUSE=off, the callee passes inlineJudge's pure-total map/fold spine checks (:6237–6296). Both B1 folds are reserved; the x/y group starts at a inside the inline. Its A7c plan frees y2 there, before z reads it. Neither a scalar return nor lack of escaping aliases makes that later read safe. Independently, placing the x/y folds BEFORE B1 is permitted by :6930; omp test mode declines fusion, leaving B1 to read the already-freed producer pool.

   **Exact fix:** represent the inlined call's end explicitly, after its full spine and evaluated scalar return, and attach cleanup there, independently of join insertion. Reuse the allocation/escape owner for that boundary; never infer return from JoinId. A conservative interim fix is to refuse cleanup/admission unless the join is proved to follow the entire inline and no intervening statement separates it from return. Add the later-map witness and the before-B1/emitter-decline witness, including a nested caller block. Correct the unconditional “inline moves no free” records/docs until this is proved. The current join can also be later than return, retaining pools across intervening statements/failures.

2. **BLOCKER — two admitted producers in one inlined call release the same pools twice in main.** Each plan receives the entire call's InlinedPools (:6978), but `taken` deduplicates PRODUCER ids, not inline-call ownership (:6910, :7043). Main has no allocation frame; freeAllocNow's None arm unconditionally emits its fallback every time (CodeGenLoopNest.fs:4964). FreeGuard only prevents recursive dispatch and is reset immediately (CodeGenBinding.fs:454); it is not a once-per-call release guard.

   Witness shape: one scalar callee takes x/y over Idx<1003> and u/v over Idx<1009>, computes a separate B1 producer/checksum for each pair, and returns their checksum sum. A main block evaluates `(kernel(x,y,u,v), reduce(x,(+)) + reduce(y,(+)) + reduce(u,(+)) + reduce(v,(+)))`. The different extents form two sibling groups; each admits its own producer and records both computed pools. With scratch reuse off, the first join releases both pools and the second releases both again. genLetChainBinding unrolls main's block without creating an allocation frame (CodeGenBinding.fs:7168–7185). G52 caching does not fix duplicate ownership: it receives the same pointer twice.

   **Exact fix:** own cleanup once per unique inlined call, not once per producer/join plan. Share one cleanup event across all plans, including main's no-frame path, and emit it at the real return boundary from NEW 1. Add the two-extents/two-plans witness with cache on/off and scratch reuse off, checking one release per allocation and gate-off equivalence.

Lifetime audit: freeAllocNow does use the SAME frame.Escapes/SuppressNames and renderFrameFrees as scope exit (:4907, :4965); tracked nonescaping allocations are suppressed by exact name after release. This prevents the ordinary single-plan function-body double free, but proves nothing about later in-scope reads. Nested let blocks inherit their surrounding frame/owner, not a new callee escape domain. Returned arrays/tuples/deferred maps cannot escape this inliner: it requires a bare scalar return (:6264), rejects capturing callable references and assignments (:6270–6276), and admits only a closed pure-total spine. Mut-parameter storage is not an IRCompute local. Scratch-reuse donors AND reusers are skipped (CodeGenBinding.fs:438), preserving their existing owner but not proving lifetime matching. Both tracked dense frees and main's fallback reach nested_array_utilities.hpp:577 / blade_pool::release (:584), so G52 retention is honored. For saxpy's tested order, B1's checksum binding precedes the free, which precedes input-join binding; that order is not enforced for general plans.

Views: direct slice-expression arguments fail inlineJudge (:6260). A named view substituted for a callee parameter is checked under its caller id by OperandNotFresh (:6893), so that inline path declines. Uninlined function regions accept their parameters, including callers' views; function-result operands are also trusted without a FreshPool proof. Thus “fresh storage” is not an interprocedural ownership theorem. Same wrapper/id/index/extent plus the no-write interval supports the read-only traversal identity; no additional view-induced miscompile established here.

Unmirrored emitter checks: actual B1 plan placement; resolved cell-map/one-level rectangular and laneable form; emitter write analysis; actual producer/fold team compatibility; BLAS in strict builds (planner's probe is conditional on b1Licensed, emitter's is not); omp test mode; lane-block divisibility; dispatch precedence; tiled/segmented/MPI slabs; streamed operands; fold-wrapper/chunk state; flat output/input storage signatures, full-depth real-array peels, positive cell count and full parallel coverage. See CodeGenCuda.fs:2101–2191, :2248, :3877–3950 and CodeGenLoopNest.fs:2743–2812, :2917. Some structural checks are implied by admission, but they are not shared predicates. LLVM additionally requires the emitted array to be AVirt/AVirtFlat with dense rank-one shape (:4719), and re-resolves leg kernels/operands (:4745). After safe cleanup, the remaining retained inline changes interpreter IR and backend optimization opportunities; test interp alone cannot validate it, while optimizerGates/opt-diff can. A total admitted callee supplies no reachable language-panic frame to lose. Docs must stop promising that both lanes necessarily carry every planned inline.

Route-pin limits: 026's C++ stores-before-idiv assertion and LLVM extra-two-accumulators/extra-two-GEPs assertion operate on that fixture. llvmFusedLoop (:1891) inspects raw emitter IR BEFORE clang/vectorization; it need not survive vectorization and would not recognize optimized vector IR. It selects the pool-storing block with most accumulators, rather than tracing named inputs, but is adequate for this small fixture. 029's explicit no-inline emission check is C++ only (:2021); both lanes' runtime fault comparisons additionally require `at kernel`, establishing the failure frame but not an LLVM structural route assertion. These tests do not cover the new cleanup witnesses; 026 has no inlined callee pools at all.

Hook verification:

| Claim | Verified hook at 9f2877a7 | Result |
|---|---|---|
| Shared admission and transitive dominance | src/Optimize.fs:2783, :2876 | Both kernels/seeds; payload write audit retained. |
| Retained-inline decision and optimizer gate | src/Optimize.fs:7043, :7059, :181; src/Lowering.fs:2964 | Planner-only commitment; BLAS function hook installed per lowering. |
| C++ legs fed inside producer | src/CodeGenCuda.fs:2214; src/CodeGenLoopNest.fs:2584 | Own strict chain or licensed state; fallback remains. |
| Join publication and cleanup | src/CodeGenBinding.fs:436, :456; src/CodeGenLoopNest.fs:4962 | Publication present; cleanup boundary and uniqueness broken. |
| LLVM fused twin and fallback | src/EmitLlvm.fs:4691, :4783, :4219 | Per-leg state/flags and fallback; no added pool free. |

Scope: no emission change found for programs without an admitted A7c plan; freeAllocNow has one plan-guarded call site, blasGateProbe is only consulted by routeProblem, and optimizerGates registration is unchanged by this fix. The blockers affect admitted plans, including plans whose emitter later declines.

---

## codex-diff-W2-4-3

_formerly `bench/notes/round3/reviews/codex-diff-W2-4-3.md`_

VERDICT: DO NOT MERGE

Second-look NEW 1: PARTIAL — cleanup is independent of JoinId, but the chosen boundary can be deleted, bypassed by function-body emission, or followed by CSE-created pool reads.
Second-look NEW 2: RESOLVED — legInlines is keyed by call, and one boundary entry owns both plans' pools; the previous per-plan double release in main is removed.

Read-only review of 58efa62d..70234a4a; locations below are at 70234a4a unless stated otherwise. No builds, tests, execution or measurements. Witnesses are source-derived, not executed. Reported footers/ASan/timings are unverified. git diff --check is clean.

1. **NEW — BLOCKER: the sibling rewrite itself can delete the recorded boundary.** Optimize.fs:7096 selects the last SfInl unit BEFORE rebuilding the joins. An existing tuple join accepted as a member is subsequently put in dropUnits (:7140), and omitted from the output (:7159). InlineReturnTable is not repaired. An unused join passes the projection-only test: all = proj = 0 (:6681–6697).

   Witness: use 031's two extents, but let kernel compute only the Ix y2/B1 s, then end its let spine with an UNUSED explicit per-leg join over the Jx u/v inputs, and return the earlier scalar s. The caller supplies x/y sums and u/v sums. The Ix input-leg plan retains the inline; W1-D combines the existing Jx join with the caller's Jx folds. The boundary key is the unused Jx join, which is dropped. In main no return-boundary release is emitted, whereas the original kernel frees y2. This is the atom-return case: selecting the last let rather than s's definition is initially correct, but that last let does not survive.

   **Exact fix:** create a dedicated, non-removable return-boundary event after join reconstruction and scalar-return evaluation, or explicitly transfer the event to a surviving marker at the same position. Never transfer it to the earlier replacement join. Add the two-extents/unused-final-join/earlier-atom-return witness and assert a release in main with no use afterward.

2. **NEW — BLOCKER: CSE can create reads after the boundary free; it can also delete the boundary key.** Lowering.fs:3136 records the table, then :3172 runs cseModule. CSE's available-value map ignores cleanup events (Optimize.fs:1726–1741); it drops duplicate lets and redirects their reads (:1751–1777). Neither boundary keys nor pool ownership participate.

   Live-storage witness: adapt eplReturnPointFree's driver (OptimizeTests.fs:2104). Use one named pure scalar cell kernel for y2's map. After `k = kernel(x, y)` and the caller's x/y sums, compute z with the SAME named map over x/y, then fold z with a cube cell kernel (so this is not another B1 checksum). Pin serial repro, BLAS off, scratch reuse off; leave CSE on. The inlined y2 and caller z have equal compute expressions. CSE removes z and redirects its later fold to y2. The surviving boundary is the first B1 s, which does route through genBinding and frees y2. The later cube fold reads that freed pool; scope escape analysis does not protect ordinary scalar reads. Gate-off has separate function storage and cannot make this cross-call merge.

   Independently, a callee with an earlier `let r = helper(a)` and final scalar return `helper(a)` gets a fresh return let from inlineJudge; CSE can drop that duplicate call. Use a named pure scalar helper to avoid source-location differences in arithmetic nodes. The table retains the dead id and cleanup disappears/delays.

   **Exact fix:** make CSE respect the cleanup region: preserve the boundary event and prevent storage equivalences from crossing its lifetime, including merging a callee pool into earlier caller storage. Do not merely rename the key to the earlier equivalent scalar. Alternatively perform final ownership/liveness planning after CSE and prove all aliases dead at release. Add both witnesses, asserting route, boundary survival, and allocation-specific last-use/release order with CSE on/off.

3. **NEW — BLOCKER: ordinary function-body return lets never reach the new first dispatch arm.** CodeGenBinding.fs:333 is the only boundary consumer. genFuncBodyScoped dispatches its own lets (CodeGen.fs:214, :232); ordinary scalar expressions use exprToCpp directly (:749–756), and plain computed maps also bypass genBinding (:329–358). The existing function-body test ends at an IRReduceCompute and exercises its special genBinding route (:375–395), hiding the gap.

   Witness: change that test's kernel return from `s` to `s + 1.0`. inlineJudge appends a fresh scalar return let (Optimize.fs:6320–6322); the driver emits it through CodeGen.fs:753, so no boundary release occurs. y2 survives until driver exit. Put a runtime integer division by zero in the caller after k (and after its input sums): the original kernel already released y2; the inline never releases it because panic uses _Exit (blade_runtime.hpp:369–391). This violates the requested caller-after-boundary failure case even with CSE and scratch reuse off.

   **Exact fix:** consume boundary events in the common per-let emission wrapper, including genFuncBodyScoped and any expression/loop-body let renderer, after that let's value has been evaluated. Share the release helper and ensure a binding routed through genBinding does not fire twice. Add non-atom scalar-return and earlier-atom-return cases in a function caller, plus a caller panic after return; assert release before the failing binding.

4. **NEW — BLOCKER, previously missed residual: LLVM does free the uninlined callee's pools.** The new assertion at Optimize.fs:2686's preceding type comment / :7019 and G69 is false. A noncapturing scalar call is outlined (EmitLlvm.fs:3797, :3852). Its body opens a pool scope (:7605); B1 allocates through allocPool (:4721), which registers pools in an active scope (:2068). Scalar return calls popPoolScope with emitFrees=true (:7649), emitting blade_free (:1352). A7c moves that allocation into main, where no pool scope records it, and LLVM never reads InlineReturnTable. Thus 019/saxpy's original callee releases y2; its A7c inline retains it to process exit. In a function caller it instead survives to that caller's return.

   This predates 70234a4a; it is an omitted arm of the lifetime repair, not a newly introduced LLVM implementation change. The second review's agreement with the no-free claim was incorrect. The added 029 test checks refusal/frame/accumulators, not allocation or release; 030/031's new ASan execution is C++ only.

   **Exact fix:** implement the same explicit return-boundary ownership in LLVM, removing/clearing released pools from enclosing scope tracking, or decline this retained-inline route there until supported. Pin actual allocation/free relationships and order on 019 and 031, gate on/off, in main and a function caller. Correct G69/docs/records and withdraw the LLVM lifetime-matched attribution until measured with matching cleanup; a no-free assertion would pin the wrong behavior.

Other requested checks:

- Before reconstruction, buildUnits appends each call's complete spine contiguously (Optimize.fs:6372–6374); distinct calls do not interleave their spines. Non-atom returns are evaluated in a final let; atom returns select the last spine let, even when returning an earlier scalar. `y2(0)` is not an atom, but the current sfTotal whitelist does not admit an IRIndex return (:6127–6156), so that specific call is refused. Freeze recognition, slice-write fusion and invariant hoisting precede this table (Lowering.fs:3129; Optimize.fs:7222–7229). IRLift preserves enclosing let ids (IRLift.fs:696–709). Those facts do not repair findings 1–2.
- If the entry survives and its binding reaches dispatch, cleanup runs after binding code, regardless of emitter acceptance (CodeGenBinding.fs:339–355). An inner join and B1 consumer bind their published scalar results before a later boundary (:462, :513). Caller input joins may remain AFTER the free: they consume published scalars or caller input arrays, not the released callee output. There is no requirement that every caller join precede cleanup. Admitted inline bodies must be pure-total (:6306); a language-panic callee is refused. Caller-after-return cleanup equivalence fails in findings 1–4.
- No admitted leg plan means no legInlines entry (:7074–7085); existing W1-D inlining can still occur independently (:7088). The nested block in 031 retains its call. The two-plan dictionary fixes duplicate ownership even if one emitter declines, subject to boundary reachability above.
- Both C++ cleanup branches use ordinary deallocate: freeAllocNow reuses renderFrameFrees and suppresses the tracked name (CodeGenLoopNest.fs:4963–4975); main emits the dense fallback. Rank-one deallocate calls blade_pool::release (nested_array_utilities.hpp:577–584). The 58efa62d..8fd38425 retention diff changes caps, not release ownership. G52 does not repair any missing boundary or live-storage free.
- Scratch reuse is NOT lifetime-neutral. Its plan runs after inlining over the entire caller function (Lowering.fs:3173; Optimize.fs:591), can join callee/caller pools, and the boundary exempts both donors and reusers (CodeGenBinding.fs:341–347). The donor is freed at the enclosing function's exit, or retained if escaping (CodeGen.fs:757–774; CodeGenLoopNest.fs:4511). Even a reuse group wholly inside the former callee now survives to the caller's exit. This exemption is inherited from 58efa62d, not introduced here; the new witnesses disable it. Limit the once-at-return claim accordingly, or retain lifetime regions in reuse planning.
- The W1-D join-driven inline exists in BOTH e4dec0ba and 667250cd: keptInlines comes from joined members and buildUnits flattens them without a return cleanup event. Its main-scope retention is pre-existing. Reported 68 versus 65 means three more release calls on THAT run; it is not a general “never fewer” theorem, nor proof of ownership or last-use correctness.
- releasesSafe (OptimizeTests.fs:2085–2098) checks one textual free and no subsequent token naming each commented pool; 030/031 also require 3/2 pools. It would catch the original main-scope early/double releases on those fixtures. It does not test deleted keys, scalar function-body dispatch, CSE-created aliases, or scratch reuse. The omp-test-mode arm does not assert an actual decline/no-fused-route, only safe releases; add that route assertion. No new LLVM no-free pin exists.

Hook verification (five claims):

| Claim | Verified hook | Result |
|---|---|---|
| Shared admission and route mirror | src/Optimize.fs:2807, :7048 | Admission retained; BLAS refusal now applies in repro too. |
| One retained-inline boundary per call; reset | src/Optimize.fs:7081, :7099; src/Lowering.fs:2964 | One owner, but recorded before destructive rewrite/CSE. |
| C++ input-leg feeding | src/CodeGenCuda.fs:2214; src/CodeGenLoopNest.fs:2584 | Existing producer feed retained. |
| C++ publication and return release | src/CodeGenBinding.fs:333, :462, :513; src/CodeGenLoopNest.fs:4963 | Publication precedes reached boundary cleanup; dispatch coverage incomplete. |
| LLVM fused twin and fallback/lifetime | src/EmitLlvm.fs:4691, :4203, :7649 | Fused/fallback routes retained; original scalar return frees pools, new boundary unimplemented. |

Scope: no new emission change found for programs that had no admitted A7c plan at 58efa62d; strict BLAS-on/unrequested-B1 cases can lose a formerly admitted plan under :7048, but saxpy's B1 has an omp request so that predicate does not remove its repro plan. No timing or full-suite conclusion is independently verified.

---

## codex-wave2-merged

_formerly `bench/notes/round3/reviews/codex-wave2-merged.md`_

VERDICT: HOLD — the merged lifetime/CSE interaction remains unsafe; current records contradict both dispatch and cleanup.

Scope: 667250cd..3a3925ac, HEAD verified at 3a3925ac. Read CLAUDE.md first; file/git inspection only, including existing harness artifacts. No builds, tests, Blade execution, compiler execution, or new measurements. Reported gates are not independently verified. Locations below refer to the merged tree.

1. **BLOCKER — cleanup is not an invariant of the integrated pipeline.** W2-4 records a boundary before its own join deletion (`src/Optimize.fs:7564–7575`, `:7613`, `:7632`), before CSE (`src/Lowering.fs:3136`, `:3172`) and before pool reuse (`:3173`). CSE still merges equal stored computations and rewrites their readers without seeing cleanup (`src/Optimize.fs:1735–1779`): a caller map equal to the inlined producer can become a read of its already-released pool. This is a source-derived miscompile path, not cured by the green gates. The other three known holes remain: a dropped final join/duplicate return deletes the boundary key; ordinary function-body scalar returns bypass `genBinding` (`src/CodeGen.fs:749–756`); LLVM never consumes InlineReturnTable although outlined scalar functions free scoped pools (`src/EmitLlvm.fs:7730`, `:7769–7774`, `:1352`).

   W2-6 changes retention caps, not ownership. A reached C++ boundary uses `freeAllocNow`, which suppresses that name's scope-exit free (`src/CodeGenLoopNest.fs:4976–4987`); one entry per call avoids the former per-plan double release. No independent double-free introduced by the retention arm was found. It nevertheless makes a premature release cache-visible and potentially reusable; it cannot repair last-use correctness. Reuse participants are exempted from boundary cleanup (`src/CodeGenBinding.fs:341–347`), so even a reached boundary does not establish the universal lifetime claim.

   **Exact fix:** land the queued round-4 lifetime event, inserted after join reconstruction and return evaluation; make CSE, reuse, every C++ renderer and LLVM honor its ownership region, or decline the retained inline. Preserve/validate last use, forbid cross-boundary storage equivalences/reuse, and remove released LLVM pools from scope tracking. Cover deleted-final-join, duplicate-return, later equal caller map, non-atom function return, caller panic and both lanes' allocation/free relationships. Until then, default-disable the affected retained-inline admission if this wave must ship.

2. **BLOCKER — the permanent and emitted records did not receive the integration corrections claimed in the brief.** G35 (`bench/notes/gap-register.md:85`) contains no pull qualification and still says default scatter / `prefer` row slots for comoment3. G44/G66 say pull > scatter > row slots, which the code implements. G69 (`:119`), CLAUDE.md's epilogue-legs row (`:206`), `docs/features.md:281`, and the actual planner string (`src/Optimize.fs:7492`) still say LLVM frees no pool in either form; G69 still labels the LLVM measurements LIFETIME-MATCHED. G69 contains no third-look/four-hole/round-4 qualification. The 3a3925ac commit message explicitly withdraws these claims. A commit-message caveat does not repair a false plan record.

   **Exact fix:** rewrite G35's current policy with pull first and `prefer` effective only after pull declines/is disabled; mark its old comparisons historical. Replace the unconditional cleanup claims in G69, CLAUDE.md, features and planner output with the actual supported/unsupported cases and the four open holes, until finding 1 is closed. Withdraw generic LLVM lifetime-matched attribution. Preserve G52's correct process-wide LLVM budget/no-runtime-record distinction. Also qualify the header's old no-events/byte-identical-record comment (`src/cpp/blade_run_record.hpp:251–254`): policy.pool now changes even default v1 records. CLAUDE.md:224's “anything else” pool-policy grammar must include the accepted `true`/`false` spellings (`src/Toolchain.fs:249–250`).

3. **HIGH — the A/B labels overstate causal isolation.** `tables-w2.md` calls r3w2prefer the old precedence and the comoment3 comparison “vs row slots”. In the merged code that arm still pulls; its 1.017 [0.975, 1.054] is not a pull-vs-G35 measurement. For antisym, the 0.857 old/new result is indicative, and gfortran-expert also improves by 0.857 while ifx-expert improves by 0.846. The actual default-vs-anti_g=1 comparison is only 1.039 [0.992, 1.069] in the inverse direction, indicative. The full old/new gain cannot be assigned to row grouping.

   **Exact fix:** relabel prefer as a scatter/row-slot tie-breaker subordinate to pull; obtain the G35 arm with BOTH `BLADE_ORBIT_PULL=0` and `BLADE_FOLD_ROW_SLOTS=prefer`, asserting its route. Keep antisym's direct knob contrast and old/new result indicative; do not promote 0.857 to an isolated anti_g win in RESULTS 17.2.

4. **HIGH — the specific saxpy retained-free caveat conflicts with the harness artifacts.** The archived OLD serial-repro LLVM file in `C:/bt_r2ab/r3w1/bench/results/2026-10-21-LAPTOP-350H6LDB/session-161051/build/saxpy/full/serial-repro-native/blade-llvm/generated/saxpy.ll` allocates y2 in main at line 174 and has NO `blade_free` declaration or call. NEW session-162330's corresponding file allocates it in main at line 129 and also has no free. OLD licensed likewise has no free. Thus these recorded old/new cells do not exhibit the claimed moved 16,000,024-byte return free. This does not exonerate the general outlined-callee lifetime defect in finding 1; it prevents transferring that defect's proposed timing decomposition to this particular baseline.

   **Exact fix:** reconcile the third-look witness/binary/source identity with these archived compile artifacts before publication. For the artifacts inspected, no removed y2 free contributes to the old/new delta; neither artifact frees it. If a different baseline actually frees it, the share of 10.79→9.588 ms (1.202 ms) attributable to that free is unmeasured—do not subtract the earlier ~0.5 ms single probe or call the whole delta fusion. Finish the legs-off arm and compare matched cleanup/routes. Its session-175202 was incomplete on inspection (no archived generated file/summary); the supplied tables still lack its valid cells.

5. **HIGH — one new runtime knob escapes suite isolation.** `BLADE_MEMBER_VERBOSE` is read at each build (`src/cpp/blade_member.h:296–302`) and changes stderr in both lanes, but appears in none of optimizerGates, optimizerGateMirrors, suiteClearedKnobs or suitePinnedKnobs. Tests explicitly turn it on; an ambient value remains inherited elsewhere.

   **Exact fix:** add it to suiteClearedKnobs and document its runtime/report-only status and spellings alongside the membership knob. No executable-cache key is needed for this runtime switch.

Integration audit:

- **Dispatch/census:** the actual emission is ordered, but the plan is not one final decision object. A rank-3 pull_min decline records the pull refusal (`CodeGenBinding.fs:5174`), then a row-slot refusal/yield (`:5233`/`:5247`), then one applied scatter (`:6024`, `:4760`). The scatter emits one omp census note. These are consistent candidate records, not three executed forms; do not count every fold-form row as a selected form. A single finalized form record with nested refusal evidence would remove this ambiguity. The runtime forms census describes pull3's actual-team branch, not every statically selected serial scatter.
- **Membership + T4c:** membership is planned after sibling rewriting/CSE and before compaction (`Lowering.fs:3172–3193`). Compaction records side-table plans; it does not delete/rename the mask host. The predicate remains in the mask pass (including gather mode); the later companion pass reads stored mask bits. C++ wraps the actual binding emission with build/free (`CodeGen.fs:2230–2234`, split path `:2348–2350`); LLVM brackets bindNamed/emitCompoundInit (`EmitLlvm.fs:8543–8590`). The certificate keys BOTH host and kernel (`Optimize.fs:4040`). Route rows now include index-types/537–539 (`tests/LlvmTests.fs:601–604`), checked by the host-placement machinery. No lost window found.
- **Retained inline + membership/T4c:** a contains-bearing callee does not pass sfTotal/inlineValueOk (`Optimize.fs:6600–6629`, `:6737–6780`); capturing probe lambdas also fail inlineJudge. Thus no admitted A7c inline-host identity case exists here. compound is top-level-only; a callee-local shared compound owner cannot enter this inline. Shared companions borrow one index and own distinct buffers; top-level registration emits no frees (`CodeGenBinding.fs:2770`, `:2784–2785`). Do not describe this as executed scoped-single-free coverage.
- **Shared analyses:** one provenance engine, exprMayWriteWith, backs scheduleMayWriteIn, its callback-aware adapter and codegen. Unknown calls are writes; copied dense local aliases/local mutation are distinguished from external mutation. The adapters are not identical: the callback-aware schedule factory collects raw b.Value (`Optimize.fs:1583`), membership's manual callback collection uses bindingPayloadExpr (`:5240`); ordinary schedule checks have no callback candidates. A7c additionally ORs the inherited sibling writesIn closure into that predicate (`:6561–6587`, `:7383`); its CSE facts mark any IRAssign/IRForRange writing (`:843`), unlike the provenance engine's local-write rule. These are conservative extra refusals here, not evidence of an unsafe admitted writer. Consolidate callback input collection and label conservative repeatability restrictions separately from observable writes. Membership and T4c genuinely share storedArrayProof (`:3684`, `:4160`, `:5230`); probeCertificate consumes that admission and adds scalar totality, not another write/storage proof. A7c intentionally requires FRESH storage (`:7395–7409`): parameters/compute/literal/known function result accepted, aliases/views refused; that is stricter than “stored”.
- **Knobs/cache:** listed emission knobs are covered: MEMBER_HOIST/EPILOGUE_LEGS in optimizerGates; compaction `unshared` in optimizerGateMirrors; ORBIT_PULL/PRINT_BITS/FOLD_ROW_SLOTS/TILE_TUNE in suiteClearedKnobs; POOL_RETAIN pinned off to override toolchain-file settings. “Exactly one” is not literal: POOL_RETAIN is both cleared and pinned, and COMPACTION_FUSION is a gate plus value mirrors, intentionally. No listed emission knob lacks cache coverage: C++ keys emitted text + runtime headers (`Build.fs:722–738`), LLVM keys IR + shim identity, including retention defines and blade_member.h (`:1246–1290`, `:1371`). PRINT_BITS is runtime-only. Plan records exist for scheduling/build knobs; runtime print/report instruments do not choose a compiler plan.
- **One JSON record:** comma composition is coherent, version remains 1, policy.pool is additive/optional for old readers. Parsed fixtures cover none/member/forms/both plus all three under retention (`tests/RunRecordTests.fs:388–441`, `:593–615`). The three-way fixture checks field presence/counts and selected pool values; detailed route fields and version are checked in separate fixtures, not all reasserted in that one. No three-way JSON defect found.

A/B reading for RESULTS 17.2 (existing data only; LIVE is the table's statistical classification, not a correctness endorsement):

| Claim | What the evidence permits |
|---|---|
| Pairkernel mt8 repro | LIVE old/new 0.846 [0.838, 0.861]; expert ratio about 0.68→0.80, still a loss. prefer contrast indicative; pull-off control agrees. |
| Comoment3 mt8 repro | LIVE old/new 0.737 [0.716, 0.760], expert ratios 1.15/0.96→1.55/1.31. Scatter/pull = 1.156 [1.108, 1.207] is indicative (equivalently pull/scatter ≈0.865); prefer is still pull. |
| Antisym | mt8 0.857 and serial 1.007 LLVM / 1.011 C++ are indicative. Archived serial emission is BYTE-IDENTICAL: old session-154013 vs new sessions-154436/155331/160214; SHA256 cpp A20155BCE93204E0… / ll 045035D9347F6551… in every case. This is not changed serial tile emission. Broad expert drift and pooled-session CI make a small environmental shift plausible; runtime headers/shim changed, so source identity alone does not prove identical machine code or prove “noise”. |
| Saxpy | Serial old/new LLVM 0.889/0.893 and C++ 0.923/0.947 are LIVE observations. Isolated legs attribution awaits the missing arm and finding 4's reconciliation; not a general lifetime-matched compiler claim. mt8 repro control 0.998 is LIVE; licensed control indicative. |
| Haloadjoint | First measurements are losses: serial selected LLVM expert ratios ≈0.52/0.65 LIVE; mt8 licensed C++ ≈0.42/0.64 LIVE; mt8 repro ≈0.44/0.65 indicative. A capped n=50,021 new workload, not a wave-2 speedup or fully threaded adjoint. G67/G68 remain open. |
| Retention | Stencil repro 8.427→7.801 ms, 0.926 LIVE; licensed 0.920 indicative. Recur licensed 9.127→6.764 ms, 0.741, and repro 12.38→9.524 ms, 0.769, both indicative. Direction agrees with G52's probe 11.5–11.8→7.9–8.2 (≈0.69), not its exact baseline/magnitude. Peak +0.1–0.2 MB is observed here, not a memory bound; tables do not provide a wall_s comparison. Report the opt-in arm separately: relocated release, not less work or an additive A7c gain. |
| Gram | Indicative control, CI overlaps equality; not a LIVE no-regression proof. T1/T4c have no new harness expert-ratio evidence here. |

Five hook checks:

| Claim | Exact merged hook | Result |
|---|---|---|
| Pull precedes prefer; pull_min falls through | src/CodeGenBinding.fs:5168, :5231, :5998 | Ordered selection; multiple candidate records, one chosen emission. |
| Certified probe keeps its host window | src/Optimize.fs:4040; src/CodeGen.fs:2230; src/EmitLlvm.fs:8587 | Host/kernel identity retained through compaction. |
| Boundary release does not repeat at scope exit | src/CodeGenBinding.fs:350; src/CodeGenLoopNest.fs:4985 | Suppression exists; boundary survival/last use still unproved. |
| Shared selection has one index owner | src/CodeGenBinding.fs:2784 | Companion frees exclude index; index registration only with last companion, no top-level executed free. |
| Pool + forms + membership coexist | tests/RunRecordTests.fs:593; src/cpp/blade_run_record.hpp:239, :255, :286 | Parsed three-way fixture; additive version-1 record. |

(g) No new default scheduling/text change found for a plain program outside these admissions; universal runtime/header/shim changes do affect its executable/cache identity and float-print instrument, and its default C++ run record now gains policy.pool—whole-program byte identity with wave 1 is not a valid claim.

---

## codex-diff-W2-4-4

_formerly `bench/notes/round3/reviews/codex-diff-W2-4-4.md` (scratchpad; appended after the condensation)_

VERDICT: DO NOT MERGE

Hole 1 — PARTIAL: the release statement survives a dropped final join, but its required spineStart still names a removable let; a dropped FIRST join now produces BL6001 (NEW 2).
Hole 2 — PARTIAL: available.Clear() really prevents later-to-earlier CSE across the event, and named pools cannot merge in either direction; spineStart is unpinned, and multiple calls in one statement defeat the claimed per-call return-let pin (NEW 2–3).
Hole 3 — CLOSED for the reported ordinary function-body case: isStatementShaped routes the unit event through genBindingDispatch, independently of the return value's renderer. This is not universal expression-renderer support.
Hole 4 — PARTIAL: tracked free/null and direct main free are implemented for LISTED pools; an admitted array rename allocates an UNLISTED LLVM pool and retains it beyond the original return (NEW 1).

Read-only review of 654760e6..19898911; parent and target worktree HEAD verified. Locations below are at 19898911. Read CLAUDE.md, the round-4 brief and both prior reviews. Files/git only; no builds, tests, compiler execution or measurements. Reported footers and ASan results are unverified. git diff --check is clean.

1. NEW — BLOCKER: the event's ownership list omits admitted copy-on-alias allocations.

   Optimize.fs:7474–7480 collects ONLY IRCompute lets. inlineValueOk accepts an array IRVar through sfTotal (:6731–6741, :6598–6600); the producer-input freshness check concerns the producer's input operands, not every unrelated callee let. LLVM bindNamed copies a stored array rename (:4262–4270); copyArr allocates through allocPool (:4996–5005). The original scalar callee frees that allocation at :7800. The inline's event never names it: in main it is retained to exit; in a function caller it survives until the caller returns, and remains outstanding if that caller panics first. IRValidate cannot detect an allocation absent from pools.

   Source-derived witness: in 019's kernel, after s, add `let spare = x` and return `s + Float64(extents(spare))`. Keep the caller's input folds, serial repro, BLAS off, reuse off. This leaves A7c's producer/legs unchanged. The IRVar let is not CSE-eliminated (Optimize.fs:1664–1667), and LLVM emits its copy even if the extent is constant. Expected missing boundary free: spare's 1003-cell Float64 pool, 8024 payload bytes. No execution claimed.

   Exact fix: represent the inline's complete allocation ownership, including emitted rename copies, and release those owners at its boundary; alternatively decline an inline whose allocation forms are not covered. Do NOT indiscriminately add every array-typed alias to pools: C++ freshening does not propagate MutableArrayLets (IRMono.fs:1869–1892), and its copy decision differs (CodeGen.fs:490; CodeGenBinding.fs:6653), so a blind alias free can reclaim caller storage. Preserve copy/ownership metadata consistently. Add a main and a function-caller-panic witness, matching allocation identities to boundary frees in both lanes. This is an additional residual lifetime hole, not a newly introduced copy rule.

2. NEW — HIGH: spineStart recreates the dangling-key defect as a validation failure.

   Optimize.fs:7568–7571 chooses the FIRST original inline binder before dropUnits is applied. Existing joined tuples are deleted at :7613/:7638. IRValidate.fs:463–464 then requires that exact binder to exist. Witness: move 032's unused Jx join from the END to the START of kernel, leaving its Ix producer/B1 and earlier-scalar return intact. The Jx join still combines with the caller's Jx folds and is dropped; the Ix input-leg plan still retains the inline. The event survives, but the valid admitted program fails BL6001 because its start vanished.

   Independently, CSE pins pool IDs and the preceding let, NOT start (Optimize.fs:1754–1756). A first scalar helper call equal to an earlier caller value can be removed at :1772–1773, producing the same rejection. The new “honoured by every pass”/closed-hole records are false for this shape.

   Exact fix: make region identity durable—e.g. a dedicated non-removable start marker, or a guaranteed surviving/pinned owned-pool anchor with a correspondingly precise validator. Establish it against the reconstructed spine, and preserve it through CSE. Add both first-join-deleted and first-scalar-CSE witnesses; assert validation succeeds with the plan admitted.

3. NEW — MEDIUM: calls sharing one statement share a delayed cleanup position.

   buildUnits appends ALL called spines before SfOrig i (Optimize.fs:6839–6841). Events are accumulated by i, not by the boundary between (i,c) spines (:7566–7572), and all are emitted only upon SfOrig i (:7629–7634). Thus call A's pools remain live throughout call B's entire inlined body, contrary to the claimed original return boundary.

   Witness shape: two scalar callees in one tuple/arithmetic statement, one with an Ix producer/B1, one with a Jx producer/B1, plus each extent's caller input-sum pair; use distinct extents as in 031/032 so both plans admit independently. Both events land after B. This changes peak live storage versus the outlined calls; no read-after-release or double free established here. The preceding-let pin also pins B's return for A's event, then A's EVENT for B's event, not each call's return.

   Exact fix: emit each event after its own reconstructed spine and evaluated return, before the next call's spine, keyed by (statement, call ordinal). Preserve each explicit return binder rather than inferring it from adjacency to a group of events. Add the two-calls/one-statement case with free(A) before alloc(B), and a true duplicate-helper return assertion.

4. NEW — MEDIUM: check 5 is a reference scan, not a complete storage-liveness check.

   IRValidate.fs:447–458 follows module callables and captures, but never resolves local let definitions. A hand-built spine `p = compute(...); d = deferred_map(p); release(p); reduce(d)` passes this new check: its suffix names d, not p. Likewise `t = (p,...)` before the event and a projection of t afterward hides p. A later mut-call through such an alias is not traced to its storage either. Direct pool arguments/projections and direct callable captures ARE found. The interval test also accepts any ID bound since start; it does not prove ownership or allocation-list completeness.

   No current admitted source route to these hidden post-event reads was established: the inliner refuses deferred-map/capturing-lambda bodies, returns a bare scalar, and refuses assignments. This is a missing invariant backstop, not evidence of a reachable use-after-free.

   Exact fix: track local storage provenance and deferred dependencies through aliases/projections and callable arguments; distinguish stored scalar results from deferred reads so published B1 scalars remain legal. Validate ownership/list completeness against the inline region. Add deferred, tuple-alias and mut-argument negative fixtures alongside the current direct-reference fixture; qualify the records until that check exists.

Remaining requested checks:

- POSITION / surviving joins: reconstruction inserts a group at its earliest member (Optimize.fs:7608–7609), never after that member. Any member reading a callee-local pool originates inside that callee; the scalar return cannot expose the pool to a caller leg. A7c explicitly rejects output legs (:2858–2862). Consequently I found no surviving rebuilt join moved from a pool-reading callee statement to AFTER its event. This does not cure omitted ownership, lost start IDs, or the multiple-call delay above.
- PASS ORDER / CSE: freeze recognition precedes lowering's optimizer; slice-write, row-fold and invariant hoisting precede sibling sharing (Optimize.fs:7701–7708). Afterward: IRLift, forceCallableBody, CSE, then side-table planners (Lowering.fs:3135–3202). No separate post-sibling IRLift CSE/equivalence table found. IRLift preserves enclosing let IDs (:700–713). available.Clear at Optimize.fs:1775 empties the actual lookup dictionary, not merely a pool subset; pinned pools are neither substituted nor installed as representatives. For a SINGLE call, its final fresh return let is retained even when equal to an earlier helper call. Existing substitutions remain, but none can target a named pool because those never enter available.
- C++ RENDERERS: module/block let chains use genLetChainBinding -> genBinding (:7349); ordinary named-function bodies use CodeGen.fs:662–698; statement loop bodies use CodeGenBinding.fs:7005/:7175. Normal/split/self-contained program assembly shares these paths (CodeGen.fs:3854/:3860/:3949). MPI/CUDA HOST assembly also uses them; sibling sharing excludes CUDA/MPI function regions (Optimize.fs:7662), and producer admission excludes device/MPI kernels. Raw expression/IIFE chains use renderLetExpr/renderUnitStmts (CodeGenExpr.fs:1748/:1832), which do NOT consume the event: their fallback reaches BL7001. Generic dense nests route these materializing bodies through an outlined callable (CodeGenExprSupport.fs:1262/:1311); raw ragged/wreath body renderers remain expression paths. No new event-specific BL7001 on a previously supported admitted source program was established. “Every other let renderer through isStatementShaped” is nevertheless not literally the implementation; document the reachable routes and excluded contexts.
- LLVM OWNERS / EXITS: a listed plain computed map allocates once and registers one slot in the innermost scope (EmitLlvm.fs:2061–2072). CSE/reuse cannot give that named owner another allocation slot; no admissible named-pool phi/rebinding path found. The event loads/frees/nulls that slot (:4140–4157); normal pop and break cleanup load the slot (:1349/:2832), and blade_free(NULL) is a no-op (blade_llvm_shim.c:405–407). Compiler refusal discards the attempted LLVM emission; CLI fallback generates C++ from the same IR (CliCommands.fs:402–414), including its event. This establishes the listed-pool mechanism, not the completeness missing in NEW 1.
- FAILURE / TWINS: 034's event precedes the caller's division by construction in both binders. A potentially failing callee is refused by inlineJudge's closed-total check (Optimize.fs:6773); its frame and original cleanup behavior remain. Both interpreter and compiler consume the post-optimizer tree; Interp/Core.fs:1048 accepts the event as VUnit. REPL/notebook evaluation uses the same lowered/validated IR through Interp/Repl.fs:103/:110/:147 and ReplSession.fs:1262/:1289. interp cannot detect a shared optimizer error or native lifetime defect; opt-diff includes EPILOGUE_LEGS (Optimize.fs:190) and can catch gate-dependent validation failure, but value equality alone cannot certify frees.
- REUSE / RECORDS / TEST LIMITS: G69 (bench/notes/gap-register.md:119) records 035's +1 allocation/+1 release and unchanged outstanding storage, explicitly lists W1-D return events as NOT BUILT, and keeps LLVM lifetime-matched saxpy attribution WITHDRAWN. Those qualifications are present. Its closed-class claim, the unconditional pool-release claims at Optimize.fs:7486, EmitLlvm.fs:4248, CLAUDE.md:206 and docs/features.md:281 still overstate NEW 1–3; amend them with the fixes. 033 returns `bump(s) + 0.0 * r`, not the identical `bump(s)` RHS its comment describes; no assertion pins that duplicate return's survival. llvmReleasesSafe (OptimizeTests.fs:2586) counts annotated frees and checks later uses of the allocation register, but does not independently require tracking-slot nulling or compare release position with blade_idiv. Add those structural assertions; do not treat the listed footers as an ownership proof.

Hook verification (five claims):

| Claim | File:line hooks | Result |
|---|---|---|
| Shared admission and per-build BLAS gate | src/Optimize.fs:2810, :2858, :7515 | Output legs refused; BLAS checked in repro and licensed builds. |
| One durable boundary and CSE barrier | src/Optimize.fs:7571, :7632, :1754, :1775 | Event survives; ownership anchor and per-call position do not satisfy the full claim. |
| C++ feed, publication and release dispatch | src/CodeGenCuda.fs:2214; src/CodeGenLoopNest.fs:2584, :4976; src/CodeGenBinding.fs:458, :336; src/CodeGen.fs:698 | Existing feed/publication retained; ordinary function/main release reaches the new arm. |
| LLVM ownership and release | src/EmitLlvm.fs:4140, :4268, :5001, :7800; src/CliCommands.fs:414 | Listed slots free/null correctly; rename-copy allocations are omitted. |
| Validation and interpreter twin | src/IRValidate.fs:447, :463; src/Interp/Core.fs:1048; src/Lowering.fs:3171 | Stale start rejects; indirect local storage reads are not closed over; interpreter consumes the same optimized IR. |

Scope: no production emission or validation change found for a program with NO admitted A7c retained-inline plan at 654760e6; check 5 adds errors only when an event exists. The admitted class remains unsafe/incomplete as above. Only this review file was written; no full-suite, timing or execution conclusion.

---

## codex-diff-W2-4-5

_formerly `bench/notes/round3/reviews/codex-diff-W2-4-5.md` (scratchpad; appended after the condensation)_

VERDICT: DO NOT MERGE

Fourth-look NEW 1 — PARTIAL: top-level aliases decline; scalar-valued RHSs still hide unlisted LLVM allocations (NEW 1 below).
Fourth-look NEW 2 — CLOSED: the first listed owned-pool binder survives join rebuilding, IRLift and CSE; no subsequent producer-pool DCE found.
Fourth-look NEW 3 — CLOSED for delayed A7c cleanup: the tentative spine detects other contributing inlinable calls, including ones W1-D would retain; its “call stays” record is overbroad (NEW 3).
Fourth-look NEW 4 — CLOSED as the documented ONE-LEVEL backstop, not a storage-liveness or ownership-completeness proof. That bounded backstop is acceptable once admission actually closes allocation shapes.

Read-only inspection of 19898911..23fe111c and 654760e6..23fe111c. Target HEAD/parents verified; 361504b6's intervening changes are bench/notes only. Locations below are at 23fe111c. Read the requested CLAUDE.md and fourth review. No builds, tests, Blade execution, g++, or measurements; reported footers unverified. git diff --check is clean. Stored graft index unavailable; no refreshing graft command run.

1. NEW — BLOCKER: “storage-free result type” does not mean allocation-free evaluation; nested array lets bypass the whitelist.

   Optimize.fs:7560-7579 checks each OUTER SfInl unit, not all lets evaluated by its RHS. sfTotal explicitly accepts IRIf and IRLet recursively (:6611-6612), including array IRVar values (:6598). Source-derived witness: keep 036's producer/B1 and caller, replacing its top-level spare/return with:

   ```blade
   let extra = if s > 0.0 then {
       let spare = x
       Float64(extents(spare))
   } else 0.0
   s + extra
   ```

   extra has scalar type, is not a deferred form, and passes :7578. The fresh return also passes. The only listed pool remains y2 (:7474-7480). IRLift preserves conditional-arm lets (IRLift.fs:1126-1138); CSE does not eliminate the branch's alias. LLVM emitExpr evaluates the arm's IRLet without opening a pool scope (EmitLlvm.fs:2714-2737); bindNamed copies spare (:4268), through copyArr/allocPool (:5001/:2061). Thus an additional 1003-double pool is unlisted: main retains it; a function caller retains it until its own exit, or through a subsequent panic. Original outlined kernel frees it at :7800. The event and validator know only y2. This is a source-derived allocation path, not a claimed executed witness. The C++ branch has an ordinary IRVar/IIFE rendering (CodeGenExpr.fs:1799-1822), so no unsupported array-construction renderer is required.

   Exact fix: replace the result-type test with a closed evaluation-shape check. Recursively inspect nested lets, branches, tuple/projection operands and other evaluated children, declining any caller-scope allocation outside the enumerated top-level owned maps. An array alias inside a scalar RHS must decline too. Recheck the normalized/freshened body or ensure later normalization cannot expose unchecked allocations. Add this main witness and 037's failing-driver variant, pinning the ownership decline and surviving calls in both lanes. Do not add the alias blindly to pools: C++ and LLVM alias ownership still differ.

2. NEW — HIGH: String is admitted as “storage-free,” contrary to the cross-lane allocation claim.

   holdsNoStorage is explicitly an ARRAY-storage predicate (Optimize.fs:932-938); IRTScalar ETString returns true. sfTotal accepts a string literal, and CSE preserves literal lets (:6597/:1666). A long string let added to 019's callee is admitted. C++ emits an owning std::string (CodeGenExprSupport.fs:595), whose heap storage/destruction follows its enclosing scope; LLVM interns the literal (EmitLlvm.fs:2658). The string is neither a listed dense pool nor allocation-free in both lanes. This is an owning C++ value counterexample, not an assertion that std::string uses Blade's pool allocator.

   Exact fix: use a dedicated recursively defined allocation-free value-type whitelist here, excluding String (including tuples containing it), rather than widening/changing the shared holdsNoStorage predicate. Alternatively explicitly narrow the lifetime contract to Blade array pools and document the changed string lifetime; that does not satisfy the requested “allocates in neither lane” guarantee. Add a long-string decline fixture. Remove “every allocation the admitted callee makes” at EmitLlvm.fs:4248 until the actual contract is proved.

3. NEW — HIGH: decline records promise a surviving call that W1-D can still remove.

   Optimize.fs:7586 says “nothing is inlined for the legs, the call stays.” But :7603-7606 independently retains every inline contributing a W1-D join. Modify 038's first kernel to add 036's top-level spare and extent use: the A7c ownership check declines, while its Jx join still causes W1-D to retain the inline. No A7c event is installed. This preserves pre-existing W1-D behavior, but contradicts the new decline text and the scope paragraphs in CLAUDE.md:206, docs/features.md:281 and G69 (bench/notes/gap-register.md:119). The planner's applied evidence (:7486) makes the same unconditional promise about other callees/second calls.

   Exact fix: say “not retained for A7c; independent sibling-fold inlining is unchanged,” or produce the final call-survival record after keptInlines is known. Add the alias-plus-Jx-join witness and assert both the A7c decline and retained W1-D inline. Do not disable W1-D to make the wording true. Also correct CLAUDE.md:206's old “inline's first let” description and its blanket 030-039 ASan/both-lane release-testing claim: the checked-in ASan list remains 030/031/032/033/035 (CellEpilogueTests.fs:669), and 036-039 ASan was explicitly not run. The new SCOPE paragraphs qualify the intended release promise, but do not cure NEW 1-2. Raw renderers still refuse; “every let renderer” should name the supported statement paths, not imply expression-renderer support.

4. NEW — HIGH: the new witnesses do not establish the claimed LLVM routes.

   OptimizeTests.fs:2798-2807 checks a planner decline substring, no applied plan, C++ leg absence/CALL survival, and only absence of an LLVM A7c comment. It never asserts an LLVM call instruction or LLVM leg absence; it discards LLVM's decisions. 039 requires only ONE matching decline, not one for each call. eplDurableStart (:2828-2838) checks validation, two planner admissions and single C++ releases in main/driver; it never emits LLVM. eplLlvmReleaseOrder's case list (:2659-2663) excludes 038. The fault/frame twin loop includes 026/029/034, not 037 (CellEpilogueTests.fs:610-612); 037's corpus pins do not assert “at driver.” Value comparisons cannot fill these structural gaps.

   Exact fix: scope call assertions to main/bl_driver and require actual LLVM calls to kernel/ka/kb, with each expected decline and no A7c feed/publication/release. For 038 require both planner admissions, successful validation, actual lane placement (or explicitly pinned fallback), and exactly one matching allocation/release in each of LLVM main and bl_driver, using llvmReleasesSafe including slot nulling. Add 037 to the gate-on/off fault/site/frame twin with “at driver.” Keep 033's new true-duplicate assertion and 034's free-before-idiv assertion.

Requested boundary checks:

- The whitelist does run on freshened units AFTER inlineJudge appends the non-atom return let (Optimize.fs:6777-6792). That return's TYPE is bare scalar; its evaluation is not allocation-free by construction (NEW 1). Top-level array-typed IRApp results, array tuples/projections and aliases fail ownership; IRMask and Bool-producing maps fail the earlier totality/plainLeaf gates. Ordinary scalar extents and numeric scalar tuples allocate no array pool; IRRange is not admitted by sfTotal despite its special type representation. This whitelist is conservative, not the exact complement of all allocating forms.
- The anchor is taken from the tentative pool list, not literally recomputed after dropUnits; this is safe for the listed maps because only join members are dropped (:7661-7664), IRLift preserves their IDs, and CSE pins them (:1754-1764). Post-sibling passes are IRLift/forcing/CSE then side-table planners (Lowering.fs:3135-3202). B1 still emits the producer pool (EmitLlvm.fs:4872); no unread-producer DCE was found. A callee containing only storage-free lets cannot supply this inline's forced producer, hence cannot enter legInlines through that producer. Empty pool lists produce no event (:7619-7623).
- The second-call test examines ALL tentative units, before W1-D's final rebuild, so it includes a second W1-D-contributing spine. Declining A7c does not change W1-D's keep set. A zero-let identity call contributes no units, but also contributes no allocating spine whose execution would delay cleanup.
- One-level provenance (IRValidate.fs:470-482) is ordinarily only a hand-built-IR backstop: caller lets preceding the call cannot name its fresh pool IDs, and the intended whitelist excludes storage-bearing aliases inside it. Listed owned maps derived from another pool are independent allocations, not aliases. No source-produced hidden post-release read was established. NEW 1 concerns missing ownership, which this check cannot detect. Deeper provenance remains unproved; interpreter value equality cannot prove native frees.

Hook verification (five claims):

| Claim | File:line hooks | Result |
|---|---|---|
| Freshened, fail-closed admission | src/Optimize.fs:6777, :6786, :7557, :7578 | Fresh return included; scalar RHS evaluation and String escape the claimed allocation closure. |
| Durable start and CSE/reuse protection | src/Optimize.fs:7619, :7663, :1754, :632; src/Lowering.fs:3139 | Listed pool binder retained; no post-sibling pool DCE found. |
| C++ return-boundary release | src/CodeGenBinding.fs:336, :7349; src/CodeGenLoopNest.fs:4976 | Listed owners reach frame free/suppression or main dense free. Raw expression paths remain outside this mechanism. |
| LLVM allocation and release ownership | src/EmitLlvm.fs:4872, :2069, :4140, :4268, :5001 | Listed map has one slot in a function, direct pool in main; nested alias copies are unlisted. |
| Validator and interpreter twin | src/IRValidate.fs:470, :484; src/Interp/Core.fs:1048 | One-level reference backstop and no-op twin implemented; neither proves allocation-list completeness. |

Scope: no emission change found for programs with no admitted A7c retained-inline plan at the baseline; round 5 only narrows A7c admission and preserves W1-D. Interp/opt-diff implications remain round 4's; no test or timing conclusion. Only this review file was written.

---

## codex-diff-W2-4-6

_formerly `bench/notes/round3/reviews/codex-diff-W2-4-6.md` (scratchpad; appended after the condensation)_

VERDICT: DO NOT MERGE

Fifth-look NEW 1 — PARTIAL: direct nested aliases are checked; allocations behind a call evaluated in tuple position remain unlisted (NEW 1).
Fifth-look NEW 2 — PARTIAL: bare String and ordinary tuples containing String decline; IRTIdxTagged can wrap String and bypasses the new predicate (NEW 2).
Fifth-look NEW 3 — PARTIAL: the A7c/W1-D decline wording and first-owned-pool anchor are corrected; allocation and test-coverage claims remain too broad (NEW 1–4).
Fifth-look NEW 4 — PARTIAL: 038 now checks LLVM placement/release/nulling; 037/041 join the fault twins; decline byte identity is checked, but the asserted LLVM route is not, and is wrong for several fixtures (NEW 3–4).
Premise correction — PARTIAL: 019/saxpy and direct tuple-component calls expand through EmitLlvm.fs:7559/:7592, without a pool scope; ordinary noncapturing scalar calls, including 037/041 and 039's arithmetic operands, outline through :2799/:3797/:3852 and free at :7800. Array parameters and totality do NOT select these routes.

Inspected 23fe111c..5c230f74 and 654760e6..5c230f74; target HEAD and parent verified. Read the requested bench-fortran CLAUDE.md first and the fifth review. Locations below are at 5c230f74. Files/git only; no builds, tests, Blade, g++, or measurements. Reported footers remain unverified. git diff --check is clean. Existing graft index has no source cards; no refreshing query was run. 654760e6..361504b6 changes bench material only (including bench/RESULTS.md).

1. NEW — BLOCKER: a scalar call inside a scalar tuple can allocate an unlisted LLVM alias pool.

   Source-derived variant of 019, retaining its producer, B1 and caller: define this noncapturing helper before kernel, then replace kernel's final `s` with the two lines below.

   ```blade
   function extra(a: Array<Float64 like Ix>) -> Float64 = {
       let spare = a
       Float64(extents(spare))
   }
   // At the end of kernel:
   let t = (extra(x), 1.0)
   s + t[0]
   ```

   sfTotal accepts the helper's alias/extent and follows the call (Optimize.fs:6613, :6627, :6630–6636). inlineValueOk therefore accepts t (:6756). The freshened unit is a tuple of Float64; shapeProblem checks the tuple and call RESULT types, never extra's body (:7607–7621). The helper is not another original-statement SfInl: buildUnits inserts only the outer call's freshened lets (:6843–6855). IRLift preserves scalar calls in tuple components (:30, :1001–1013).

   LLVM classifyValue sends t to emitTupleParts (EmitLlvm.fs:5024); its IRTuple arm recursively visits each component (:7559), and its IRApp arm expands EVEN this scalar-returning extra (:7592–7613). That arm binds parameters and emits a shadow-frame pair, but opens/pops no pool scope. The helper's `spare` reaches bindNamed's copyArr (:4268), allocPool (:5001/:2069). Only y2 is listed by Optimize.fs:7489–7495. In main the copy survives the A7c event; inside driver it belongs to driver's scope, not kernel's return boundary. A tuple-returning helper containing the same alias also takes this path (:5043). No downstream admission rechecks the emitter expansion. This is an admitted unlisted-pool path derived from source, not an executed probe.

   Exact fix: audit resolved callable bodies transitively for allocation when their execution can expand into the caller (including scalar calls in tuple-component position and tuple-returning calls); conservatively decline unresolved/unaudited cases. Alternatively give those expansions a proven pool scope and prove returned components cannot escape it. Do not assume a scalar/tuple return implies separate cleanup. Add both main and failing-driver witnesses, pinning the decline/no-event and unchanged output, or the complete release route if implementing scopes. Do not add spare indiscriminately to the event's pool list.

   Contrast within this finding: `let t = extra(x)` with a SCALAR RHS reaches emitCall and, for a noncapturing helper, outlines and frees its alias at the helper's return (:7756/:7800). It is reachable, not allocation-free evaluation, but separately scoped. A helper building a local map/literal is refused by sfTotal's default; an array rename is not. The tuple-context variant above invalidates the builder's proposed blanket exemption.

2. NEW — HIGH: the dedicated value-type predicate still admits owning String values through index tags.

   Optimize.fs:944 accepts `IRTIdxTagged _` without inspecting its inner type. stripUnits strips ONLY unit annotations (IR.fs:1523–1526). This is not an integer-only type: Types.fs:1263–1275 expressly permits any inner type; TypeCheckInfer.fs:12994–13006 accepts string-valued EnumIdx labels. CodeGen.fs:2463–2469 emits their aliases as std::string; CodeGenState.fs:2223–2245 erases the tag to that representation. LLVM likewise peels the tag (:98), so it does not establish a cross-lane no-allocation property.

   In particular, a normal helper returning a long string-valued EnumIdx label has an IRApp result of `IRTIdxTagged(IRTScalar ETString, ...)`; a let binding that call in kernel passes both the outer predicate and the call-result check. Its C++ return value owns heap storage in the enlarged caller scope. Nested tuples containing that tagged value pass recursively too. Ordinary numeric tuples, including nested ones, are handled correctly. Nat<I> over integers is a primitive value; bare IRTNat is size_t in C++ (:2222), but LLVM scalarTyOf has no IRTNat arm (:91–100), so claiming supported emission in both lanes for every admitted type is also too strong.

   Exact fix: replace `IRTIdxTagged _ -> true` with `IRTIdxTagged(inner, _) -> go inner`; keep holdsNoStorage unchanged. Add tagged-String and nested-tuple predicate tests plus a source decline witness. Also close the literal/operand gap if retaining the literal “whole evaluation is allocation-free” claim: shapeProblem ignores IRLit/IRVar leaves, so a String literal used only inside a Bool comparison is not judged (C++ constructs std::string at CodeGenExprSupport.fs:595). Scope claims to supported lane representations, not all types accepted by the predicate.

3. NEW — HIGH: the LLVM premise/route record confuses tuple-position expansion with array-parameter calling.

   Actual route census:
   - 019, saxpy, 036, 040, 042: kernel is a direct component of a tuple. emitTupleParts expands it in place, irrespective of scalar return or array parameters; main opens no pool scope. The correction is right for these programs.
   - 037/041: driver's `let k = kernel(x, y)` is scalar. classifyValue :5044 -> emitExpr :2799 -> emitCall :3797/:3852 outlines kernel. Its scope frees y2 and spare at :7800 before driver's later failure. The fourth-look path applies here, and to an admitted clean variant of this shape; the evaluation guard is essential when the call is then inlined for A7c.
   - 039: the first tuple component is an arithmetic expression. emitTupleParts :7623 falls through to scalar emission; ka/kb within that arithmetic are outlined calls, each with its own cleanup.
   - 038's first kernel and 043 can already be optimizer-inlined for W1-D; 038's driver-side kernel2 has the ordinary scalar-call route when not retained by A7c.

   Totality affects shadow-frame retention (resolveFrames, :8679–8694), not outlining or pool-scope creation. No shadow frame does not mean no pool scope.

   Exact fix: correct G69's “total array-parameter callee” explanation and OptimizeTests.fs:2831–2833/:2848. Pin route-specific evidence: LLVM kernel calls in bl_driver for 037/041 and calls to ka/kb in main for 039; direct tuple expansion/no kernel call plus its allocations for 036/040/042. Keep the byte-identity pins: they prove no gate-induced change, not which baseline route executed. Blanket LLVM-CALL assertions from the fifth review were wrong for the direct tuple cases; blanket in-place-expansion assertions are wrong for the scalar cases.

4. NEW — HIGH: checked-in coverage claims exceed the assertions.

   CLAUDE.md:206 says “the declines 036-043 pinned as emission byte-identical” in both lanes. The equality assertions cover exactly 036/037/039/040/041/042 (OptimizeTests.fs:2810–2829/:2840–2845). 038 is admitted. 043's separate test never emits gate-off, discards LLVM decisions (:2861), and does not assert absence of applied plans; it checks one decline, a W1-D application, C++ call absence, binding-level event absence and release/leg markers (:2862–2868). G69:119 similarly says “every decline pinned”. Those statements are not supported by that test.

   Exact fix: enumerate the six equality fixtures in CLAUDE.md/G69. Add both-lane gate-off comparisons and no-applied-plan checks to 043 before claiming them. Describe 043's retained inline/alias-copy lifetime as pre-existing W1-D behavior; G69 already excludes W1-D return-boundary events from BUILT, but does not explicitly identify 043's unlisted alias copy in that exclusion. Keep ASan restricted to 030/031/032/033/035 C++; no ASan claim for 036–043. The narrowed LLVM owned-pools record (:4248) is accurate for its listed pools; the planner's whole-evaluation claim (:7501) still fails NEW 1–2.

Evaluation/rewrite audit:

- Direct nested IRLet array values, array/String IRApp results, array-bearing tuples/projections, and array/String branch results are judged and decline (:7611–7628). A direct IRMatch is already outside sfTotal. Non-owned IRCompute is either refused earlier or declined by its result type; owned forced maps are the explicit exception.
- IRMask, array literals, ranges (even though IRRange can report scalar type), zero/uninit, index/slice materializations, sort/set operations, array combinators outside the allowed forced map/fold shapes, sequence/assignment and unknown node kinds fail inlineValueOk/sfTotal's closed defaults. Deferred maps/loop values fail the ownership predicate; reading a recorded deferred value fails sfTotal. A captured lambda is refused by inlineJudge (:6777–6784).
- Stored array IRVar operands of accepted folds/maps are reads, not alias binders. Numeric literal/arithmetic/extent values and scalar tuples allocate no owned pool. Calls are the missing classification: sfTotal proves neither allocation freedom nor separate cleanup; NEW 1 supplies the uncovered execution context. String/index-tag cases are NEW 2.
- The check DOES include inlineJudge's fresh return let (:6798–6804), but runs on tentative units BEFORE finalUnits/join reconstruction (:7653/:7678–7704), not after every rewrite. The rebuild adds scalar joins/projections; the second-call check sees all original-statement tentative SfInl units (:7572), including a second W1-D contributor. It does not inspect call bodies later expanded by LLVM. Later IRLift/forcing/CSE (:3139/:3162/:3171 in Lowering.fs) do not cure that omission.
- 038's test now requires two LLVM placement records and exactly one release in main and bl_driver (:2894–2896). llvmReleasesSafe traces allocation -> tracking slot -> loaded free operand and requires that SAME slot nulled on the next line (:2601–2629); it is a textual direct-register check, not a general alias-liveness proof. 037/041 fault twins and at-driver pins are present. The decline helper checks no applied LLVM plan plus byte identity; __epl_ absence is checked only in C++, not LLVM.

Hook verification (five claims):

| Claim | File:line hooks | Result |
|---|---|---|
| Freshened allocation admission | Optimize.fs:6798, :7575, :7607; EmitLlvm.fs:7592 | Fresh return included; callable-body/tuple-context allocation escapes. |
| Durable region start and optimizer barriers | Optimize.fs:7667, :1754, :632; Lowering.fs:3139 | First listed owned-pool binder; joins cannot drop it, CSE pins it, reuse excludes it. |
| C++ boundary release/renderers | CodeGenBinding.fs:336, :7349; CodeGen.fs:662; CodeGenLoopNest.fs:4976; IRLift.fs:84 | Supported statement routes reach release; owner frame suppressed or main freed. Raw expression paths are not supported. |
| LLVM ownership and release | EmitLlvm.fs:3797, :4140, :7592, :7756, :7800 | Outlined scope and tuple expansion differ; event frees only listed pools, nulling tracked slots. |
| Validation/twin and LLVM witness | IRValidate.fs:470, :484; Interp/Core.fs:1048; OptimizeTests.fs:2624, :2895 | One-level liveness backstop/no-op twin; 038 slot/nulling checked; none proves ownership completeness. |

Scope: round 6 only narrows A7c admission; no emission change found for programs without an admitted A7c plan (decision text changes). W1-D's keep set remains independent. No new timing conclusion; saxpy remeasurement remains outstanding. Only this review file was written.

---

## codex-diff-W2-4-7

_formerly `bench/notes/round3/reviews/codex-diff-W2-4-7.md` (scratchpad; appended after the condensation)_

VERDICT: DO NOT MERGE

Sixth-look NEW 1 — PARTIAL: direct IRApp nodes, including tuple-position 044/045 and scalar-let 046, now decline; the same call hidden in a fold kernel bypasses the walk (NEW 1 below).
Sixth-look NEW 2 — CLOSED for the reported predicate defect: tags recurse through their inner type, nested tuples recurse, bare Nat declines; predicate tests cover these. Direct String literals/reads are checked. 047 is an earlier totality refusal, not an exercised tagged-String admission.
Sixth-look NEW 3 — CLOSED: tuple-component expansion versus scalar-let/arithmetic outlining is corrected and asserted per equality fixture.
Sixth-look NEW 4 — PARTIAL: the equality list and 043's gate-off/no-applied-plan assertions are fixed; 047's equal LLVM refusal is still described as identical emission (NEW 2).

Read the requested bench-fortran CLAUDE.md first. Inspected 5c230f74..ec57d7d3 and 654760e6..ec57d7d3; target HEAD and parent verified. Locations below are at ec57d7d3. Files/git only; no build, test, Blade, g++, or measurement. Reported footers were not independently executed. Diff whitespace check clean. Existing graft index has no source cards; no refreshing query was run. 654760e6..361504b6 is bench-only, including bench/RESULTS.md.

1. NEW — BLOCKER: a non-owned fold can execute an unaudited kernel containing the same tuple-position alias-copy call.

   Source-derived variant of 019 (not an executed probe): retain its inputs, producer, B1 and caller. Declare these before kernel; add `let h = reduce(aux, step, 0.0)` after its s binding and return `s + h`.

   ```blade
   type Aux = Idx<3>
   let aux: Array<Float64 like Aux> = [1.0, 2.0, 3.0]
   function extra(a: Array<Float64 like Aux>, q: Float64) -> Float64 = {
       let spare = a
       q + Float64(extents(spare))
   }
   function step(p: Float64, q: Float64) -> Float64 = {
       let t = (extra(aux, q), 1.0)
       p + q + t[0]
   }
   ```

   Admission: foldTotal accepts the stored, nonempty Aux fold and kernelTotal follows step's body via sfTotal (Optimize.fs:6711, :6647, :6635). sfTotal accepts the tuple, helper call, alias and extent. Source-level functions have empty Captures (Lowering.fs:1742), so inlineJudge's capturing-lambda check does not reject step. Aux has a different extent from Ix; h cannot join the caller's Ix legs. The q-dependent call stays in the kernel; this is not an invariant hoisted into a checked spine let.

   The allocation check visits h's IRReduce children, ending at the IRVar naming step. IR.fs:2326/:2328 and :2665 traverse expression trees; IRVar is a leaf (:2242). They do NOT resolve callable bodies. Optimize.fs:7632 ignores function-typed references; :7639 judges no kernel body. Thus neither extra's IRApp nor spare is checked. The final pool list contains y2 only (:7493).

   LLVM executes step via emitReduceCore -> applyKernel (:6861/:6023). applyKernel binds parameters and emits a shadow-frame pair, but opens no pool scope. t goes through classifyValue's tuple arm (:5024), emitTupleParts expands extra (:7592), again without a pool scope, and spare reaches copyArr (:4268/:5001). That allocation is absent from the A7c event (:4140). The counted loop opens no scope either (:1627). In main these copies remain untracked by a function scope; in a scalar-let driver variant they belong to the driver's scope after A7c inlining, whereas the outlined kernel's scope owns them with A7c off. Earlier iterations' existing tracking limitations are not claimed as newly introduced; the missing ownership census and delayed surviving copy are sufficient here. C++ calls its kernel/helper through ordinary function wrappers (CodeGenBinding.fs:4156; CodeGenExprSupport.fs:1175); this finding needs only the LLVM path.

   A direct array alias or a long String literal in step's body also bypasses the ownership walk. The latter constructs std::string in C++ (CodeGenExprSupport.fs:595). This is user callback execution, not an opaque allocation inside a trusted arithmetic builtin. The owned-map exemption also skips kernel-body ownership analysis (:7651); do not claim a map's output is necessarily its only allocation.

   EXACT FIX: resolve every executed map/fold kernel while auditing an inline, and audit its body with the allocation/evaluation rules, cycle-safe. Conservatively reject IRApp inside such a body, array aliases, String construction and other unproved allocation forms; alternatively prove transitive callable allocation/cleanup explicitly. Apply this to kernels of owned maps too, separately from accepting their output pool. Add an Aux-fold witness with this tuple helper in main and in a scalar-let driver; require decline/no event and gate-off identity, or a complete allocation/free census if implementing cleanup. Counting original statement calls or re-running the same shallow walker does not fix it.

   SURVIVES THE OPT-IN FALLBACK: YES in the explicit BLADE_EPILOGUE_LEGS=1 inline path. Disabling A7c-kept inlining by default removes this new return-boundary ownership obligation from the default. Ordinary kernel expansion/allocation remains pre-existing; no new no-inline-default read-after-release is established by this witness.

2. NEW — HIGH, record-only: 047 does not establish byte-identical LLVM emission.

   OptimizeTests.fs:2926 accepts either equal emitted modules OR equal Error strings. Its success text (:2932), CLAUDE.md:206 and G69 (bench/notes/gap-register.md:119) nevertheless include 047 in “both lanes” emission-byte-identity coverage. The reported LLVM result is a refusal/skip, not an emitted program. The assertion correctly compares that refusal; the description is stronger than the evidence.

   EXACT FIX: say “047: C++ emission identical and call retained; LLVM refusal identical in both gate states (or identical modules if subsequently supported).” Change the test's pass text and the two records. Keep 047 separate from the ten emitted equality witnesses and from ASan coverage. No compiler change required for this finding.

   SURVIVES THE OPT-IN FALLBACK: YES as a coverage/record defect; it makes no default-path memory claim.

Closure and rewrite audit:

- Numeric/Bool/complex literals, scalar arithmetic/comparison, scalar casts/libm and extents of stored variables need no heap pool/copy in either lane. Numeric tuples/projections, scalar if and nested scalar lets likewise need only value/stack storage, PROVIDED their executed operands and callable bodies satisfy that restriction. NEW 1 breaks that proviso.
- IRMatch is not presently admitted by sfTotal. Neither is a direct IRIndex, including a String-array element used inside a Bool comparison, a tagged-String element, or a partial array view. There is NO IRIndex arm in shapeProblem that catches the String result: today's protection is sfTotal's closed default (Optimize.fs:6642), not the new type test. A future indexing whitelist must add that check; 047's no-record assertion would catch widening that admits its particular kernel.
- IRReduce/IRProdSum over genuinely stored arrays use scalar/lane state; IRReduceCompute streams its plain map leaves in LLVM (:7467/:7544) and the C++ fused reduction. Kernels are executable user bodies, not traversed expression children. This is the closure hole.
- A bare deferred map/loop let in the inlined spine declines; an arbitrary deferred expression is not an accepted fold operand shape. However foldTotal's IRReduce variable arm checks plainAxis/isStreamed, not positive storage provenance or deferred membership (:6711). A named deferred operand supplied from outside the spine is not made “stored” by this test: LLVM may materialize a multiply-read name (:6716); C++ routes a deferred wrapper separately (CodeGenBinding.fs:431). Do not infer allocation freedom merely from a variable operand. These caller-owned/global materializations are not themselves proof of a new callee lifetime regression.
- Array casts cannot escape the scalar/type restrictions through an admitted scalar expression; array-bearing tuples/branches are rejected. Scalar IRCompute exists in the IR representation, but sfTotal/inlineValueOk does not admit it here; the only direct compute exception is the forced map.
- A separate omp fold inside the callee is not excluded just because the producer and riding legs are serial. Its C++ Path B form allocates a temporary partial array (CodeGenBinding.fs:4274), then deletes it at that fold's end (:4285); LLVM's ordinary fold is serial. That is additional allocation, but locally paired, not evidence of a missed return-boundary free. The records' stated builtin-trust limitation is material; allocation-free evaluation is not proved.
- The “other IRApp” ownership arm is defensive: inlineValueOk/sfTotal already reject unresolved/non-variable calls on these admitted source paths (:6635–6642). No present source witness reaches it.
- The ownership check DOES include inlineJudge's fresh return let (:6802–6808). It runs on tentative units before the final join rebuild, once; neither emitter repeats it. The one-call check (:7576) sees all original-statement tentative SfInl units (:6862), including ones W1-D will retain. W1-D has not invisibly removed a second original call before this count. Calls inside named kernels were never such units.
- Rebuilding adds joins/projections and inserts the event before the original statement (:7678/:7754); subsequent IRLift/forcing/CSE do not resolve the missed kernel-body ownership. The planner's fused-route mirror and the emitters' placement checks are not allocation audits.

Fixtures and records:

- 033 still tests the duplicate return boundary: r and the fresh return are both sqrt(s), with the latter immediately before the event. Dropping that return duplicate leaves no earlier sqrt twin for the preceding let, so the present nonLeaf/structural-equality assertion still fails that regression (OptimizeTests.fs:3008–3018). The equal caller map and post-release cube fold remain. This was never two calls to the outer kernel; it is one inlined spine with duplicate return computation. Re-pin follows solely from replacing 0.5*s+1 by sqrt(s): s=82065, so 1035390.5 - 41033.5 + sqrt(82065) = 994643.469893706.
- 038 retains two admitted regions in one compilation, one in main and one in driver; CSE's duplicate pre/h is within driver, not cross-function CSE. Both plans, both release sites, validation and an applied CSE decision remain asserted (:2967–2981). The CSE test checks a decision exists, not h's exact removed ID. Re-pin is 69234 - 5 + 2*sqrt(1006) = 69292.4350061086. No other existing 019–043 program or EXPECT pin changed in round 7.
- 044/046/048 require expanded kernel to be neither called in the selected LLVM caller nor defined anywhere; 044 requires the same for extra. 045 requires a kernel call in bl_driver; 046 requires an extra call in main. An outlined route cannot satisfy the expansion assertions (OptimizeTests.fs:2846–2869). All ten fixtures also require byte equality to gate-off and no applied LLVM plan.
- 045's fault twin runs both lanes and compares nonzero exit plus complete fault text, requiring BL8013 and “at driver” (CellEpilogueTests.fs:615–658). Its corpus also pins line 23. 047 requires NO epilogue-legs record at all (:2922), so an appearing record fails it. 043 now compares both emitted lanes with gate-off and checks no applied plan (:2884–2903); its alias-copy inline is accurately identified as pre-existing W1-D.
- Builtin-trust language is present in CLAUDE.md:206, features.md:281 and G69:119. ASan is limited to 030/031/032/033/035 C++; declines are not ASan-run. The ten emitted equality fixtures are exactly 036/037/039/040/041/042/044/045/046/048, with 043 checked separately. Remaining overclaims are NEW 1's whole-evaluation/no-user-call assertion in those records and Optimize.fs:7505, and NEW 2.
- LLVM's emitted decline record (:4248) correctly limits cleanup to the listed owned maps. Its source comment at :4133 still incorrectly says the as-written call was outlined; change that to distinguish scalar-call and tuple-component routes. The LLVM saxpy lifetime-matched remeasurement remains outstanding.

Opt-in fallback: gate only the A7c inline retention/plan for inlineKey=Some on explicit 1; preserve inlineKey=None plans for a producer already present in the region (019's second spelling). W1-D's independent keep set remains. IRReleasePools is minted only from legInlines, so without an A7c inline there is no new event for CSE to pin/clear, pool reuse to exclude, the interpreter to consume, or IRValidate check 5 to reject. Those handlers remain installed but inert for an event-free program. No additional default-path misfire found. The fallback contains NEW 1; it does not repair it.

Hook verification (five claims):

| Claim | File:line hooks | Result |
|---|---|---|
| Freshened-spine ownership and execution closure | Optimize.fs:6802, :7573, :7612; IR.fs:2242, :2326, :2665 | Fresh return checked; named kernel bodies missed. |
| Durable event and optimization barriers | Optimize.fs:7694, :1773, :1794, :632; Lowering.fs:3135, :3171 | First owned-pool anchor; listed pools/preceding return pinned, event clears CSE, reuse excludes listed pools. |
| C++ statement cleanup | IRLift.fs:87; CodeGenBinding.fs:336; CodeGenLoopNest.fs:4976 | Supported statement paths free via owning frame or main; no completeness proof for hidden allocations. |
| LLVM scopes and boundary | EmitLlvm.fs:4140, :4268, :6023, :7592, :7756, :7800 | Event frees listed pools/nulls slots; kernel/tuple expansions open no scope; outlined functions do. |
| Validation, twin and witnesses | IRValidate.fs:470, :484; Interp/Core.fs:1048; OptimizeTests.fs:2846, :2977 | One-level liveness backstop/no-op twin; route and listed-pool release checks do not census hidden pools. |

Scope: round 7 only narrows A7c admission plus decision/docs/test changes and the two fixture rewrites; no emission change found versus 5c230f74 for programs with no admitted A7c plan in either revision. Only this review file was written.
