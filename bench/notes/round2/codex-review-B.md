# Round 2 B: adversarial review

Reviewed master `4136f0b5a42f532b5d43c6e158f0961635d62b13`, the preamble, brief,
current source, supplied C++/LLVM emissions and RESULTS 14.8. No builds, timings,
test suites or repository writes. Graft access was denied by tool policy; cited
source was read directly. `B/...` below means the supplied `round2/B/` evidence;
`brief` means `round2/research/B-epilogues.md`. Verdicts authorize research/prototypes,
not default-on shipping before their acceptance experiments.

| Item | Verdict | One-line reason |
|---|---|---|
| P1 | ACCEPT WITH CHANGES | The tree is streamable, but the finish contract must handle residual whole blocks and element-width-dependent block sizes. |
| P2/B2r | ACCEPT WITH CHANGES | The ordered schedule is legal; the gap attribution, correction-15 extrapolation and default block size are unproved. |
| B1 | ACCEPT WITH CHANGES | Preserve the actual fold partition and producer rounding; P1 alone does not prove whole-kernel bit identity. |
| B2l | ACCEPT WITH CHANGES | Keep it experimental; neither the reload ceiling nor the claimed spill frequency is measured. |
| B3 | ACCEPT WITH CHANGES | Licensed row-class regrouping is legal; fix the compile-layer dependency and qualify expert equivalence and gap attribution. |
| B4a | ACCEPT WITH CHANGES | Remove the square pool through a narrowly proved packed-value path, not by broadly enabling Compound levels. |
| B4b+c | ACCEPT WITH CHANGES | Stable branchless compaction works with slack; retain index invariants, abort ordering and an honest traffic model. |
| B4d | ACCEPT WITH CHANGES | Position elision needs a complete index-use proof; allocation escape analysis alone is insufficient. |

## Numbered objections and corrections

1. **P2's headline subtracts whole-program times, not isolated chain costs.**
   At 4 GHz, 6.02 M links at 3–4 cycles cost **4.515–6.020 ms**, and 3.0 M
   links cost **2.250–3.000 ms**. The brief's 4.2–5.6 and 2.1–2.8 ms imply
   approximately 4.3 GHz, not 4 GHz (`brief:22`, `brief:23`). Recur's actual
   count is 301 × 20011 = 6,023,311; stencil's is 1499 × 2001 = 2,999,499.
   The 4.67/3.57 ms differences are **LLVM** totals from different, indicative
   sessions (`bench/RESULTS.md:2480`, `:2487`, `:2513`, `:2520`). They do not
   directly measure g++'s IIFE versus `sq<8>`.

   There is a concrete confounder: stencil also reduces its **input** rows.
   Repro uses another 1501 × 2002 ordered additions before the kernel;
   licensed adds `reassoc` (`B/stencil_rep_ser.ll:155–193` versus
   `B/stencil_lic_ser.ll:181`, `:193`; C++ twins `:305–307` versus `:308–314`).
   B2r leaves that work unchanged. The kernel row fold itself changes from a
   scalar chain to memory-parallel lanes (`B/stencil_rep_ser.cpp:249`,
   `B/stencil_lic_ser.cpp:250`; recur `:478` versus `:479`). That supports
   latency as a contributor, not the entire gap or its recoverability.
   **Correction:** time matched OLD/NEW/CTRL per lane; isolate producer,
   row-fold and input-checksum contributions. Call these totals an attribution
   hypothesis. G37 also makes historical C++ totals unsuitable as current-OLD
   measurements (`bench/notes/gap-register.md:87`).

2. **Interleaving does not remove the checksum dependency or establish 16 as optimal.**
   The 16 additions still require about 48–64 cycles. With the question's
   approximate 256-entry ROB budget, occupancy is measured in decoded work,
   not cells: a reload chain alone has 16 loads plus 16 multiplies and 16 adds
   under contract=off, before loop/address overhead. Recur's body has roughly
   25 FP operations per cell before contraction (`B/recur_rep_ser.cpp:466–475`).
   Even assuming four-wide vectorization, 16 cells cost roughly 100 vector FP
   instructions plus loads/stores, before that chain. This is an order-of-magnitude
   budget for **one, perhaps part of a second block**, not many blocks ahead;
   emitted assembly, fusion and scheduler limits decide the actual count.
   Stencil is lighter, so it can expose more independent work, but the brief
   provides no generated candidate assembly to count.

   Store-forwarded loads are not free and are only off the accumulator
   recurrence once their data is ready. Producer dependency chains, forwarding
   latency, alias checks and retirement pressure can starve the next add.
   A register-consume alternative (`c = producer; out[i] = c; acc += c*c`)
   avoids the reload and preserves the stored cell and ascending chain under
   contract=off, provided `c` has the output element type. But a scalar spelling
   may lose producer SIMD; retaining vector production and consuming lanes
   adds extraction/register costs. **Correction:** compare register-consume
   and store/reload forms, sweep 8/16/24/32/64, inspect vectorization and spills.
   Prefer registers only when producer SIMD survives. Ideal saving is bounded
   by the smaller of independent producer work and chain work; chain time
   itself remains a lower bound. No source argument proves recovery of most
   of either total gap (`brief:86–96`, `:523`).

3. **Correction 15 is evidence for a different overlap opportunity.**
   `src/microkernels/README.md:228–234` measures overlap between **adjacent
   independent output-cell chains**: 2.44 cycles/MAC versus the dependency
   control's 3.03–3.13. It does not measure a single checksum accumulator
   interleaved with vector stores. Its fold-length dependence and finite-window
   warning support doing the experiment, not predicting its gain. **Correction:**
   cite it as an analogy only. The closer recorded precedent is ifx's buffered
   64-cell ordered consumption (`bench/RESULTS.md:2422–2427`, G36 at
   `bench/notes/gap-register.md:86`), also not a measurement of these candidates.

4. **The 25%-of-gap DROP rule is not a parity criterion.**
   Recur is already 0.985/1.017 versus the experts; its nominal deficit to
   gfortran is only 0.20 ms. Stencil is 1.224/1.001, already winning/at parity
   (`bench/RESULTS.md:2513`, `:2520`). Requiring 1.2/0.9 ms can reject a useful
   repeatable gain while pretending to target parity. Conversely, 25% of a
   confounded gap says nothing about overhead on other shapes or mt8.
   **Correction:** use 25% only as the falsification bar for the advertised
   large-overlap hypothesis. Decide retention separately from a measured
   OLD/NEW total-time gain with uncertainty, no material regressions and an
   agreed maintenance-cost threshold. Prioritizing B2r above maskagg on these
   totals alone is unsupported (`brief:523`, `:559–565`).

5. **Contraction policy is acceptable; tolerance is a test, not a proof.**
   P2 may change OLD bits under repro+contract=fast without violating the
   preamble's REPRO+CONTRACT=off identity contract (`brief:171–176`;
   `PREAMBLE.md`, constraint 3). The benchmark does permit contraction and uses
   1e-12 relative checksum tolerance (`bench/harness/config.json:21–25`,
   `bench/README.md:137`). That threshold does not guarantee every changed
   contraction passes, particularly through recurrent producer arithmetic.
   **Correction:** keep seed/order and test OLD/NEW raw output bits with
   contraction off, plus actual oracle pins under fast. Preserve `where repro`
   vetoes as well as the environment switch (`src/CodeGenExprSupport.fs:1625–1648`).
   Do not substitute decimal output agreement for a raw-bit check.

6. **P1 is exactly implementable, but its tail API must be explicit.**
   `detail::fold` assigns full B=K×W blocks to all K vectors, then routes all
   remaining whole vectors through **v0**, combines v1…v7 ascending, combines
   scalar lanes ascending, then appends the <W tail
   (`src/cpp/blade_fold_lanes.hpp:218–252`; rounded squares `:279–283`).
   Keeping those live states across any number of full-B feeds reproduces
   `sq<T,K>` for arbitrary n. Reinitializing/finalizing each 256-cell block does not.

   After 256-cell producer blocks, **feed the remaining floor(r/B) whole
   blocks before finish**, or make finish perform that phase. Example n=289:
   after feeding 256, the remaining 33 must contribute one complete 32-cell
   block, then a scalar tail. Treating those 33 as only vector remainder feeds
   every vector into v0 and changes the tree. Require finish's rem<B if it only
   implements the stated remainder phase. B=32 only for double/K=8; float/K=8
   needs B=64, so a tuning rule accepting every multiple of 32 is insufficient.
   **Correction:** enforce B alignment and test 255/256/257, 287/288/289,
   multiple producer blocks, all remainders, K=4/8 and float/double, including
   non-dyadic data. The proposed 1001 case is useful but the contract must state
   why it works (`brief:58–78`, `:402–404`, `:418–421`).

7. **B1's old producer partition is irrelevant to the fold tree, but its values are not.**
   OLD separately produces y2 (`B/saxpy_lic_mt.cpp:223–231`) and folds using
   actual team size, `chunk_lo`, and thread-order combine (`:238–253`;
   `src/CodeGenBinding.fs:4062–4077`). For a pure cell map producing identical
   y2 bits, reassigning its cells to the fold's chunks is numerically harmless.
   Same actual fold team size + same chunk-local P1 states + same seed/combine
   gives the same checksum. OMP dynamic team sizing must not be assumed fixed
   merely because OMP_NUM_THREADS is fixed.

   P1's product barrier fixes **square rounding**, not producer rounding.
   Changing strip-mining/SIMD/tails can change contraction of `1.5*x+y` under
   fast; reading a stored result does not prove that result equals OLD.
   **Correction:** make identical producer values an explicit precondition of
   the bitwise claim and check y2 as well as checksum in both contraction modes.
   Keep fixed-actual-T raw-bit tests, including non-vector-aligned chunk starts.
   The old producer macro does not even spell an explicit static partition
   (`src/cpp/blade_portability.hpp:215`); do not rely on a particular remainder
   distribution as a language guarantee.

8. **B1 mt8 has a bounded opportunity; B2l's costs are estimates.**
   Chunk ownership can align first touch and checksum reads better, but still
   incurs the fresh 16 MB output allocation/stores. Non-cache-line-aligned
   chunk boundaries can share a line; there are O(T) boundaries, not a
   per-block full-array penalty. Page ownership and affinity need measurement.
   The similar serial/mt8 incrementals in `brief:283–291` are consistent with
   first-touch cost; they do not isolate or prove its dominance. Saving a reload
   and a team does not imply recovering the whole 4.79−3.19 ms difference or
   eightfold scaling. **Correction:** the ceiling is the measured separable
   reread/team share, currently unknown; keep the first-touch floor in forecasts.

   Likewise B2l's 2–4%/0.35 ms ceiling and “WILL spill once per block” are models
   (`brief:14–15`, `:150–153`, `:328–334`). Compiler allocation can keep some
   states live, reload them more often, or harm producer vectorization. P1
   moves reloads closer; it does not eliminate their load instructions.
   Keep B2l opt-in and demand assembly plus the proposed >2% measured win.

9. **B3 is licensed regrouping, but the expert does not use the same tree.**
   Splitting each class into disjoint row subsets, summing each subset, then
   combining rows and applying the existing addition-only `Total` is covered
   by `src/OrbitFold.fs:19–36`. Require every fold-level licence, original
   outer seed, and the existing antisymmetric zero/parity handling (`:49–61`).
   Do not replace the replicated off-diagonal sum with an FMA-able weighted
   multiply (`:38–47`). OLD's boustrophedon thread partials are explicit in
   `B/pairkernel_lic_mt.cpp:319–348`; changed licensed bits are permitted.

   The expert produces a packed **column**, folds it hot, and combines
   `2*w+diag` into an OpenMP thread reduction (`bench/kernels/pairkernel/expert.f90:48–71`).
   B3 produces packed rows, retains separate diagonal/off-diagonal arrays and
   combines each serially in row order. **Correction:** say “same locality and
   one-team principle,” not “exactly this schedule” (`brief:351`). Team-size
   independence follows only with a fixed row-local tree and fixed producer
   values across serial/threaded emission; test both, not just the final sum.

10. **B3's 1e-12 tolerance and 1.5 ms attribution remain empirical.**
    For finite nonnegative squares there is no cancellation amplification;
    row length ≤2503 plus a 2503-row serial combine gives a conservative new-tree
    depth around 5006 additions (gamma about 5.6e-13 in binary64, ignoring
    overflow/underflow and producer error). That makes 1e-12 plausible, not a
    bound on **NEW versus OLD** or versus another libm's expert output. OLD's
    longer per-thread class chains have a different error bound.
    **Correction:** test the full p=2503 pins and non-dyadic stress values;
    do not relax tolerance to accommodate failure.

    G32 records diagnostic ~5.4 ms producer/~1.5 ms checksum, not 1.5 ms of
    proven DRAM traffic (`bench/notes/gap-register.md:79`). The checksum phase
    includes scheduling, team costs, canonical offsets, arithmetic and memory;
    exceeding L3 capacity does not prove every byte comes from DRAM. The previous
    threaded-pass change measured no gain (`bench/RESULTS.md:2397–2398`).
    The reread is a leading hypothesis. Keep D's attribution qualified and
    measure fused hot-row work against the removed phase, not a free 1.5 ms.

11. **B3's proposed Optimize dependency cannot compile in the current layering.**
    The brief embeds `CompactClassRows of OrbitPlan` and calls
    `OrbitFold.recognise` from `Optimize.fs` (`brief:374–382`). But Optimize is
    compiled at `Blade.fsproj:141`; CodeGenExprSupport at `:397`; OrbitFold at
    `:401`. OrbitFold opens CodeGenState and CodeGenExprSupport
    (`src/OrbitFold.fs:72–73`). Moving one Compile entry is not a solution.
    **Correction:** put a lane-neutral candidate descriptor in the earlier
    planner and resolve the orbit plan at emission, or explicitly factor the
    genuinely neutral recognizer/types into an earlier dependency layer.
    This is extra design work and shared ownership with A, not just a new union case.

12. **B4a's two gates protect more than maskagg.**
    Six logical sweeps and the square pool are real: mask at
    `B/maskagg_rep_ser.cpp:234–237`; count/fill in
    `src/cpp/index_types.h:383–391`; gather at C++ `:245`; square pool `:249–254`;
    fold `:255`. The licensed OLD fold is an unseeded **eight scalar-lane**
    eager reduction (`B/maskagg_lic_ser.cpp:255`), not already `sq<double,8>`.
    Moving to P1 can therefore change licensed bits, legitimately.

    `laneableFoldLevel` also controls Path B chunk form and the ordinary
    serial lane/SIMD arm (`src/CodeGenBinding.fs:3922`, `:4008`, `:4037–4094`).
    Its exclusions include dependent/strict/fused axes, sparse/compound,
    Reynolds, MPI and halo (`src/CodeGenExprSupport.fs:2163–2174`). The broader
    input guard protects ragged/dependent/grouped/compound and non-enumerable
    sparse inputs (`src/CodeGenBinding.fs:3772–3797`). **Correction:** add an
    explicit materialized rank-1 Compound-value, trailing_stride=1 capability;
    use cardinality/data peels and preserve all other refusals. Do not admit
    `range<CompoundIdx>` drivers, outer products, different-mask co-iteration,
    trailing dimensions or joined leaves merely because one input is Compound.
    Test serial and Path B admission/refusal separately, with bounds and seeds.

13. **B4b preserves the index only if all its invariants survive.**
    The branchless loop is stable: skipped cells overwrite the current q; a kept
    cell advances q exactly once. Allocate m+1 for BOTH arrays, including m=0;
    expose only m logical y cells. Feed only indices `<q`, never the speculative
    slack slot. The final constructor must set dense extent n, cardinality m,
    and `rank_to_tuple.n=m`, with exactly the ascending kept original positions.
    These are the invariants used by `unhash`, binary search, `linearize`,
    `present` and `prefix_range` (`src/cpp/index_types.h:377–391`, `:410–443`).
    **Correction:** preserve ownership/freeing and keyed-read BL8006 semantics;
    test all-false, all-true, last-kept-early and alternating masks. “Branchless”
    describes the source idiom; verify the compiled inner loop too. Keep `keep`
    stored in v1: the preamble's named-store constraint settles that scope,
    irrespective of the brief's optional protocol question (`brief:614–616`).

14. **“BL8003 after the pass” is correct for maskagg, not a general motion rule.**
    OLD completes mask/count/fill/gather, then initializes the unseeded fold
    from the first square and aborts on empty (`B/maskagg_rep_ser.cpp:230–255`).
    With a pure total predicate and numeric stored x, B4's extra slack writes
    have no language-visible effect; an empty result still reaches BL8003 at
    the same source span before later input_checksum work. Peel the first kept
    square; never substitute a +0 seed or read y[0] when m=0.

    However, the proposed compaction planner also mentions intervening extents
    and consumer uses (`brief:393–395`), and its refusal list does not fully
    specify failure order. Repeatable is **not** total:
    `src/Effects.fs:74–85` explicitly allows MayFail; the existing hot-row
    between-check uses Repeatable (`src/Optimize.fs:1622–1626`). Moving an empty
    abort earlier across another failing expression changes which diagnostic
    occurs. Generic B1 F/seed motion has the same issue; excluding only a
    failing g is insufficient. **Correction:** compute partials early but defer
    empty checks/seed evaluation to the original consumer site where necessary;
    otherwise require non-failing motion and prove values are available at the
    producer. Add a test with an intervening competing failure and a nontrivial
    seed, not only the empty mask test.

15. **The 22/26 MB B4 forecast mixes distinct bytes with executed traffic.**
    The proposed `y[q]=v; pos[q]=i` executes stores for every input, not just
    every kept element (`brief:234`). At n≈1 M and m≈0.5 M, requested-byte
    accounting gives mask 9 MB; fused fill 1 MB keep reads + 8 MB x reads +
    **8 MB y stores**, plus **8 MB position stores** if retained; P1/P2 still
    reload about 4 MB of completed y. That is approximately **30/38 MB**
    without/with positions, before spills and metadata, not 22/26 MB.
    Repeated writes may coalesce, so this is not a DRAM-byte prediction either.
    **Correction:** distinguish executed accesses, unique cache lines, writeback/
    allocation traffic and measured misses. The 5/9 MB live-buffer allocation
    estimate is plausible (plus slack/metadata); the 6-to-2 outer-sweep claim is
    valid, but there is still epilogue consumption inside the second sweep
    (`brief:354–368`). Do not present either byte model as a measured saving.

16. **B4d requires index-demand analysis, not merely non-escape.**
    `computeScopeEscapes` protects allocation lifetimes, following assignments,
    captures and returns (`src/CodeGenLoopNest.fs:4001–4056`); it does not prove
    a local consumer never asks for coordinates. A normal `compound_index_t<1>`
    with cardinality m and missing positions cannot satisfy `unhash`, keyed
    access, presence or prefix queries (`src/cpp/index_types.h:410–443`).
    Sharing an idx also propagates demand through derived arrays
    (`src/CodeGenCuda.fs:3643–3677`). **Correction:** require a transitive census
    across aliases/shared idx, range iteration, calls, prints/displays and
    provider serialization, with unknown uses retaining positions. Represent
    “positions absent” explicitly; do not silently leave an apparently ordinary
    table with inconsistent n/cardinality. B4b+c should land independently of d.

17. **One gate is necessary but not sufficient coverage.**
    Registration in `optimizerGates` is correct (`src/Optimize.fs:77`;
    `src/CliSelfTests.fs:95`). Opt-diff emits and runs C++ off/on with all
    optimizer gates toggled under REPRO=1/CONTRACT=off
    (`tests/OptDiff.fs:91`, `:136`, `:154–165`, `:189`). Side-table-driven repro
    emission is therefore visible even without an IR rewrite. It does **not**
    exercise B1/B3/B4 licensed forms, B2l's nondefault `lanes` value, or LLVM
    off/on emission. The interpreter can validate emission-only repro schedules
    against unchanged IR, but likewise cannot exercise a licensed-only arm
    while repro is pinned.

    **Correction:** retain the separate licensed block, include B2l, and add
    explicit LLVM on/off tests plus lane parity under the identity regime.
    Add gate-isolated CTRL tests: all-gates-off also disables fusion/hot-row
    prerequisites and is not the isolated performance control. Require emitted
    needles proving every tested arm actually fires. Dyadic corpus EXPECT pins
    are appropriate; they cannot establish raw-bit identity under reassociation
    (`src/microkernels/README.md:252–258`). Supplement with non-dyadic raw-bit
    data, tails, actual team-size tests, and local `where repro` refusals.

18. **The builder split is workable; “files disjoint” is false.**
    P1+P2+B1+B2r under one owner is coherent; B4 then B3 under another is
    coherent if common planner types and gate/decision APIs have one owner.
    B1/B2 touch CodeGenBinding, CodeGenCuda, Optimize, Lowering and EmitLlvm;
    B4 also touches all five (`brief:374–460`). They overlap beyond the two
    sites claimed at `brief:573–574`. B3 additionally conflicts with A's orbit
    dispatch (`src/CodeGenBinding.fs:3744–3764`) and needs objection 11 resolved.
    E touches B2r's recur loop and row-fold plan (`brief:385–390`, `:435–437`).
    **Correction:** agree neutral plan records and emitter ownership first;
    sequence B2r before E, B1 before C's flat producer work, and B3 after A's
    dispatch contract but before D's pairkernel team reuse. Keep threaded-cell
    B2r refusal and fallback atomic with the consumer handshake. The same
    `EmittedCpp`-style success protocol must not suppress a consumer after an
    emitter declines. Defaults remain contingent on the experiments, despite
    the default-on setting proposed at `brief:396–400`.

## Disagreements I could not settle from source

- Whether g++ or LLVM hides enough producer work behind the unchanged chain,
  the best mini-block size, forwarding behavior, and whether register consume
  beats reload without losing SIMD. Needs candidate assembly and matched A/B;
  no candidate was compiled or timed for this review.
- The recoverable fraction of the repro gaps and the true mt8 saxpy first-touch
  floor. Source proves competing work exists; total-minus-twin medians do not
  isolate it. Historical round-3 figures are not fresh measurements of 4136f0b5.
- Whether B3's reread explains most of the 1.5 ms checksum phase, or whether
  scheduling/team/library effects dominate. Pool size alone cannot decide.
- Actual 1e-12 pin agreement after B3 regrouping and P2 contraction changes,
  and whole-producer fast-mode bit identity for B1/B2l. The licences settle
  legality, not those empirical comparisons.
- Whether B4d's consumer census can prove this benchmark's idx dead in both
  lanes without weakening general Compound semantics. The available escape
  helper is only one input to that proof.