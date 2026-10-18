# Review of Codex's microkernel sketches S1-S14, and the merged implementation order

Reviewer: Claude (author of `bench/notes/phase2-microkernel-plan.md`, "the plan" below).
Subject: `bench/notes/microkernel-sketches-codex.md` ("the sketches"), written from
`bench/notes/codex-sketches-brief.md`. PLANNING ONLY: nothing was compiled, built, run or
benchmarked; this file is the only one written. "Only a build can tell" marks every question
that reading cannot settle.

Two facts were checked in the source for this review because sketches lean on them:
- G25 scope (S10): Codex is right. The MATERIALIZED Path B arm already runs K-lanes inside
  each chunk (`src/CodeGenBinding.fs:3233-3245`, fold form
  `threaded(Path B chunks, K-lanes(8) per chunk)`); only the DEFERRED chunk body (archived
  `session-113651/.../gram.cpp:314-318`) is serial.
- Exe cache vs runtime changes (S7, S12): the C++ lane's `exeCacheKey` (`src/Build.fs:702-722`)
  already hashes `runtimeHeaderDigest` and the full g++ argument text, so a new runtime header
  or a `-DBLADE_POOL_CACHE_OFF` define invalidates correctly. The LLVM lane's
  `llvmExeCacheKey` (`src/Build.fs:1288`) hashes the shim SOURCE, but `buildLlvmShim`
  (`src/Build.fs:1207-1216`) reuses the shim OBJECT by timestamp alone, and a new tiles TU is
  hashed by nothing yet. Both sketches and the plan already call this out; it is item M0 below.

---

## 1. Verdicts

| # | sketch | verdict | one-line reason |
|---|---|---|---|
| S1 | packed SYRK, store mask around the 6x8 core | ACCEPT WITH CHANGES | = plan 4.1; adopt its per-row full-store test and direct interior reload; padding rationale needs correcting (below) |
| S2 | antisym 4x4 paired strict tile vs sequential 6x8 | ACCEPT WITH CHANGES | sequential 6x8 stays the default; 4x4 is a measured alternative (its load-ceiling arithmetic understates Zen 3) |
| S3 | comoment3 fixed-i, 6-prefix x 8-k simplex tile | ACCEPT | = plan 4.2, with the same factor-order and divide-once rules; waste formula is the right thing to count |
| S4 | explicit vector accumulators for licensed prodsum | ACCEPT WITH CHANGES | = plan 4.3 plus a correct self-dot 8-chain variant; switch the site only after the gemv-fiber re-measure |
| S5 | fixed-rank canonical reads, checked flat offsets, bind-once | ACCEPT | = plan 4.4a; strict-legal, local, also the repro fix |
| S6 | orbit-class licensed checksum | ACCEPT | = plan 4.4b, with an addition-only epilogue that is strictly better than the plan's `3.0 * S` form |
| S7 | retained numeric pools, ownership unchanged | ACCEPT WITH CHANGES | = plan 4.5a; its 1.28 MB budget is fitted to recur, the plan's 32 MB x threads too large: settle on 8 MB per thread |
| S8 | RK4 fused into trajectory rows | ACCEPT WITH CHANGES | = plan 4.5b + 4.5c; split into three deliverables; its 15-25 ms forecast replaces the plan's 11-13 |
| S9 | rank-1 mask compaction with prefix counts | ACCEPT WITH CHANGES | absent from the plan, correctly added; C++ lane first, LLVM needs a new representation |
| S10 | SIMD inside deferred Path B chunks | ACCEPT WITH CHANGES | real but mt8-only and licensed-only; changes threaded bits; low priority |
| S11 | producer/checksum fusion incl. threads | ACCEPT WITH CHANGES | only after S6; MUST combine per-job states in job order, else threaded bits become schedule-dependent |
| S12 | one LLVM tile core, policy-keyed objects | ACCEPT | = the plan's resolution of the IR-vs-C question; adds the honest-memory-effects rule |
| S13 | strict hot-row checksum scheduling | ACCEPT | = plan 4.6(i); legal in repro; small payoff |
| S14 | input-row sums folded into packing | REJECT (defer) | <= 0.64 ms illustrative payoff; couples an unrelated fold into the tile ABI; changes default-build input-checksum bits |

Counts: ACCEPT 5 (S3, S5, S6, S12, S13); ACCEPT WITH CHANGES 8 (S1, S2, S4, S7, S8, S9, S10,
S11); REJECT 1 (S14).

---

## 2. Per-sketch review

Criteria applied to each: (a) correctness -- strict association, seeds, tails, the empty last
row of a strict triangle, the rank-3 simplex; (b) Zen 3 register / load-port budget; (c)
legality under `BLADE_FP_REPRO=1` vs the licensed default; (d) mt8 Path B; (e) exe cache and
sanitizer builds; (f) whether the forecast is derived honestly from measured rates.

### S1 packed SYRK -- ACCEPT WITH CHANGES

(a) Correct. `row_base<S>(p,i) = choose2(p+1-S) - choose2(p+1-S-i)` expands to
`i(2p-i+1)/2` (S = 0) and `i(2p-i-1)/2` (S = 1), the emitted layout. Lanes form an offset only
after `j >= i+S` (no out-of-range pointer: the plan's round-1 blocker is not present here). The
per-ROW full-store test `nr == 8 && j0 >= i+S` is finer than the plan's whole-tile `full` flag:
on a straddling tile the upper rows still take vector stores. Adopt it. Partial C is reloaded as
the next K block's initial accumulator, never `C += block` -- correct. The strict p = 1 /
empty last row is handled by `mr`/mask (no row read). Change 1: the sketch's generic scalar
reload must not ship as the interior path (Codex says so itself); emit full and edge variants
separately. Change 2: the stated reason for not padding the sample chain ("extra +0 terms can
alter signed zero and exception behaviour") does not hold for the padding actually at issue: a
+0.0-seeded chain cannot hold -0 under round-to-nearest (x + (-x) = +0, +0 + (-0) = +0), and
`0 * 0` raises no flag, so zero-padding BOTH operands is exact (the expert relies on it,
`gram/expert.f90` "fma(0, 0, acc) = acc exactly"). Not padding is still the right choice: it is
free (the K tail is a shorter `kc`), so the rule stays and only the rationale is corrected.
(b) 15 ymm, 0.667 loads/FMA -- agrees with the plan. KC = 192 vs 256: only a build can tell.
(c) strict-legal; ON in repro; proven bitwise R_interp under contract=off. (d) a map, not a
fold: Path B not involved; one owner per cell per pc. (e) C++ header change covered by
`runtimeHeaderDigest`. (f) Honest: 70.5-78.8 ms from the measured expert rate, "80-100 ms"
total, and it prices output traffic over 4 K blocks (71.8 MB) which the plan under-counted.

### S2 antisym 4x4 paired vs sequential 6x8 -- ACCEPT WITH CHANGES

(a) Correct: `sum(X_i Y_j) - sum(X_j Y_i)`, Q kept as `X_j * Y_i` (vector first), subtraction
once after both chains, P/Q partials stored separately between K blocks, X = pool and Y = X + n
with `ldx = 2n`, nothing stored at i = p-1. (b) The sketch prices the 4x4 at a "load ceiling
2/1.25 = 1.6 FMAs/cycle", using the brief's 2 loads/cycle. On Zen 3 the 8 broadcasts are 64-bit
loads, which can issue on the third load AGU (two 256-bit loads per cycle plus narrower ones,
three load uops per cycle): 2 ymm + 8 broadcast = 10 load uops in ~3.3 cycles against 8 FMAs in
4 cycles -- FMA-bound IN PRINCIPLE, so the sketch understates the 4x4. Against that, it has
exactly 8 chains = 4-cycle latency x 2 pipes: zero slack, so any scheduling hiccup loses lanes.
Who is right between "4x4 paired" and "sequential 6x8": ONLY A BUILD CAN TELL. Change: the plan's
sequential 6x8 (0.667 loads/FMA, 12 chains) stays the default; the 4x4 is an explicit measured
alternative and the preferred candidate for contract=off builds (11 live ymm, so no spill risk
where the 6x8 has a product temporary as its 16th register). (c) strict-legal both forms.
(f) Honest: 31.8-35.5 ms arithmetic, 34-42 ms total.

### S3 comoment3 simplex tile -- ACCEPT

Identical in substance to plan 4.2: W = round(X_i X_j) in source order, reused over all k
panels; `w * xk` keeps `(a*b)*c`; divide once, never by reciprocal, never pre-scaled W, and no
"divisor 0 = no scale" sentinel; K-blocked partials reloaded UNSCALED. Store mask `k >= j && k < p`
is right for the inclusive simplex; strict rank-3 correctly declared out of scope. (b) 15 ymm;
W 9.6 KB + Xk micro-panel 12.9 KB = 22.5 KB in L1; it correctly notes the two WHOLE
representations exceed L2 together while the hot set does not. (f) Honest, and its exact waste
expression `48 * sum_i sum_j0 (ceil(p/8) - floor(j0/8))` is what the implementer should
evaluate before quoting a time (the plan's bound is < 24%).

### S4 vector prodsum -- ACCEPT WITH CHANGES

(a) `dot4` is the plan's 4.3 with an overflow-safe bound (`n - t >= 16`) -- adopt that
spelling. New and correct: a SELF-dot (`prodsum(row, row)`, one physical stream) is 1 load per
FMA, so it can feed both FMA pipes and 8 chains can pay; the plan's arity rule would have given
it 4. Admit only on proof that both operands are the SAME expression (same IR var, same view).
(b) 4 chains + 2-3 inputs + 1 temp, or 8 + 1 + 1 for self-dot: fits. (c) LICENSED only; repro
keeps the serial chain; changes default-build bits. Change: do not switch the
`src/CodeGenExpr.fs:412-420` site until the gemv-fiber shape (`CodeGenExpr.fs:386-408`) is
re-measured -- that measurement is why simd is the site's form today. (f) Honest: "up to ~1.9x
kernel headroom" from 4.4 vs 8.4 G FMA/s, explicitly not stacked on S1-S3.

### S5 canonical reads -- ACCEPT

Same helper as plan 4.4a (`read3`'s strict branch is the plan's `sym3_off(a, b-1, c-2, n-2)`),
with two points worth keeping: the runtime owner of the sort is
`nested_array_utilities.hpp:707-720` (not `index_types.h` as the brief says), and the flat base
for possibly-empty storage must come from an allocation-owned base, not a `pool_base` spine
walk. Strict-legal, local, ON in repro; its benefit in the LLVM lane is bind-once only. Forecast
(~49 / ~42 ms C++) is the same extrapolation the plan uses and is labelled as one.

### S6 orbit-class checksum -- ACCEPT (and it corrects the plan)

The class partition (`i==j==k` w1; `i==j<k` and `i<j==k` w3; `i<j<k` w6) and the row-header
classification are right. Its epilogue `three3 = (s3+s3)+s3; result = (s1+three3)+(three6+three6)`
is BITWISE the same value as the plan's `S1 + 3.0*S3 + 6.0*S6` without contraction (doubling is
exact, `(x+x)+x` rounds once like `3x`) -- but it is SAFER under the user default
`-ffp-contract=fast`: the plan's form contains compiler-introduced products `3.0*S3`, which a
compiler may contract into `fma(3.0, S3, S1)`, a single rounding that is NOT any association of
the logical multiset. Codex is right; the plan's 4.4b must adopt the addition-only epilogue. The
whitelist (real `x*x`/`abs`, Hermitian `abs` only), seed rules (none or +0.0, inner seeds
decline), logical-empty rules (strict p = 1 rank 2 is a non-empty domain of zeros) match the
plan after its round-2 fixes. (c) LICENSED only; OFF in repro (repro keeps S5's logical walk).
(d) its class partials inherit Path B's fixed-team determinism in mt8. (f) More careful than the
plan: pool reads priced at 0.34 / 1.25 ms (comoment3 / pairkernel) at an illustrative 20 GB/s,
where the plan said "~0.5 ms" for both.

### S7 retained pools -- ACCEPT WITH CHANGES

(a) Correct ownership contract: exact `actual_cells` at release, typed per element, true base in
the LLVM header, `delete[]`/`new[]` pairing kept, skeleton rows unchanged, the multidimensional
`total == 0` sentinel case (`nested_array_utilities.hpp:408-412`) kept OUT of the cache. (e)
Cache-off must be a real define and the shim object must be re-keyed (agreed; M0). Change:
budget. The sketch proposes ~1.28 MB, "adequate for eight 160,088-byte pools" -- that is fitted
to one benchmark and would fall off a cliff for a step with nine temporaries or a 2003-wide
field. The plan's 32 MB per thread is 256 MB at mt8. Settle at 8 MB per thread, ONE budget
across element types (the plan's round-2 `tl_retained`, which already avoids the
empty-cache-eviction bug), max retained block 4 MB, and treat both numbers as measured policy.
(f) Honest: "20-40 ms" is wider than the plan's 20-30 and better supported; adopt it.

### S8 RK4 into trajectory rows -- ACCEPT WITH CHANGES

(a) The fused `step_into` preserves the source DAG with scalar locals (k1..k3 multi-use) and
factor parentheses; definite-initialisation conditions (decline on `while` freeze, whole-prefix
escape, indirect index, incomplete seed coverage; `nt = 0` skips the seed copy) and the borrowed
destination match the plan. Change: ship as THREE items -- zero-fill elision (desugar-local),
destination passing (ABI), pointwise fusion (pass) -- because they have different risk and the
first is strict-legal and local. (c) strict-legal; but under `-ffp-contract=fast` scalarising
formerly-stored intermediates can expose NEW contractions across stage boundaries (`const
double` is no barrier), so DEFAULT-build bits may change; contract=off stays bitwise. For this
kernel's DAG every product already sits in a single-use position inside one statement, so I
expect no change, but that is for the build to show. (f) Codex is right and the plan was not:
the measured 2.5 ns/cell-step x 6,003,300 cell-steps = ~15 ms; nothing measured supports the
plan's 11-13 ms. Adopt 15-25 ms.

### S9 rank-1 compaction -- ACCEPT WITH CHANGES

Not in the plan (the brief for it covered six kernels); correctly diagnosed from the pilot
(77.8 ns per kept cell = ~38.9 ms of maskagg's ~62 ms incremental). Prefix-count / selected-
coordinate representation keeps Compound's reverse lookup and index provenance; stable
order; no atomics; the parallel variant's overstore warning is right. Changes: (1) C++ lane
only at first -- the LLVM lane needs IRMask plus an owning compact descriptor, which is a NEW
REPRESENTATION, not a runtime swap; (2) the `y*y` temporary fusion is a separate item (G10/G8
class), do not bundle it. Bits: none change (copies). Forecast ~23-30 ms incremental is from the
pilot, labelled as such -- honest.

### S10 SIMD inside deferred Path B chunks -- ACCEPT WITH CHANGES

Correct about the remaining gap (deferred chunks only) and about seeds (user seed once in the
final combine, lanes seeded from their first element when no identity). Changes: (1) it changes
mt8 licensed bits (new chunk-interior tree) -- deterministic for a fixed actual team, as today;
(2) the proposed partition `lo = tid*q + min(tid, rem)` replaces today's chunk rule; keep ONE
partition function shared with the materialized arm so the two Path B forms cannot drift; (3)
lowest priority among licensed items: no measured share supports a millisecond figure (Codex
says so).

### S11 producer/checksum fusion -- ACCEPT WITH CHANGES

Correct that it applies only after S6 proves the class sum, only on the last K block after scale
or P - Q, never on partials, with named outputs still stored. Change (mandatory): the sketch
allows a dynamic job schedule whose worker states are combined "legal but schedule-dependent".
That would make threaded checksums differ RUN TO RUN at a fixed team size -- weaker than today's
Path B guarantee (fixed team => fixed bits, `CodeGenBinding.fs:3233-3235`). Require one small
class state per job, combined in job order. Payoff after S6 is 0.3-1.3 ms (its own honest
figure): low priority.

### S12 LLVM tile core -- ACCEPT

Same resolution as the plan (shared pure arithmetic core; lane drivers own scratch and threads;
policy- and identity-keyed objects; identity in the exe key). Its additional rule is right and
should be added to the plan: the helper declaration in the `.ll` must carry truthful memory
effects (writes output and scratch, not `readnone`; `nosync` only while the whole
implementation is serial). Proposal A (vector IR) remains the fallback.

### S13 strict hot-row scheduling -- ACCEPT

Adjacent row loops keep each row fold's own order, contraction and vectorisation, and the outer
fold's seed and ascending order; legal in repro; distinct from S11 (no weighting, no mirrored
reads). Its warning against fusing a scalar checksum recurrence into the vectorised stencil
producer under repro is right. ~1-2 ms on stencil; recur re-assessed after S7/S8.

### S14 input sums folded into packing -- REJECT (defer)

Correctness of the sketch is fine (first-element seed, per-pc continuation, X/Y kept apart).
Rejected for now because: (1) payoff is at most ~0.64 ms (gram) by its own budget, below the
measurement noise of the drift-flagged blocks; (2) it couples an unrelated fold into the tile
driver's ABI and packing loop, precisely the code that needs raw-bit isolation; (3) in the
DEFAULT build today's input checksum is a licensed `K-lanes(8)` tree (`plan.txt` fold-form
lines), so a serial per-row sum in the pack CHANGES printed `input_checksum` bits unless it
re-creates that tree. Revisit after the tiles are stable. The recur `u0 -> __v364 -> row 0` copy
cleanup it mentions is fine to fold into M3.

---

## 3. Changes to observable values or printed bits

Under the bench harness these are all within the regime tolerances (`bench/README.md` 5.4); the
corpus `// EXPECT:` pins read at 1e-9. "Bits" means the printed 15-digit text may move in the
last digit.

| item | default (licensed) build | `BLADE_FP_REPRO=1` (+ contract off) | mt8 |
|---|---|---|---|
| S1/S2/S3 tiles | CHANGE (today's reassociated dot -> serial chain) | unchanged (proven under off; verified under fast) | unchanged per team size (cells not split) |
| S4 vector prodsum | CHANGE (new tree) | unchanged (not applied) | n/a |
| S5 canonical reads | unchanged | unchanged | unchanged |
| S6 orbit checksum | CHANGE (class regrouping) | unchanged (not applied) | fixed-team deterministic |
| S7 pool cache, M0 | unchanged | unchanged | unchanged |
| S8 zero-fill elision, destination passing | unchanged | unchanged | n/a |
| S8 pointwise fusion | MAY CHANGE under fast contraction (cross-stage fusion) | unchanged under off | n/a |
| S9 compaction | unchanged | unchanged | unchanged |
| S10 chunk SIMD | unchanged serial; n/a | unchanged | CHANGE (new chunk tree) |
| S11 fused checksum | CHANGE (same as S6) | not applied | must stay fixed-team deterministic (job-order combine) |
| S13 row scheduling | unchanged | unchanged | unchanged |
| S14 (rejected) | would CHANGE input_checksum | unchanged | -- |

Program OUTPUT text otherwise unchanged; `blade plan` gains decision lines (route assertions).

---

## 4. Disagreements between the sketches and the plan, settled

1. Antisym schedule (S2 4x4 paired vs plan 4.1b sequential 6x8): only a build can tell. Default
   sequential 6x8; 4x4 as the contract=off / short-n candidate. Both of us mis-stated a ceiling:
   Codex priced the 4x4 with 2 loads/cycle (Zen 3 issues the narrow broadcasts on a third load
   AGU), I gave the 6x8 no warning that the 4x4 is FMA-bound in principle.
2. Orbit epilogue (S6 addition tree vs plan `w * S`): Codex is right. Same value without
   contraction; the plan's form exposes compiler-introduced products to contraction under the
   default `fast`, which is not an association. The plan's 4.4b is superseded on this point.
3. recur after fusion (S8 15-25 ms vs plan 11-13 ms): Codex is right; only 2.5 ns/cell-step is
   measured. After the cache alone: S7's 20-40 ms over the plan's 20-30 ms.
4. Retention budget (S7 ~1.28 MB vs plan 32 MB/thread): neither. 8 MB per thread, one budget
   across types, max block 4 MB; tune by measurement.
5. Self-dot accumulators (S4): Codex adds a correct case (1 load per FMA => 8 chains) the plan's
   arity rule missed. Adopt; build decides whether it earns its code.
6. Sample-chain padding (S1): agree on the rule (do not pad), disagree on the reason: 0 x 0
   padding of a +0-seeded chain is exact and flag-free; the rule stands because it is free.
7. G25 scope (S10): Codex is right (materialized Path B already has K-lanes; verified).
8. Threaded fusion determinism (S11): the sketch tolerated schedule-dependent bits; I require
   job-order combine, so mt8 keeps today's fixed-team guarantee.
9. maskagg (S9): absent from the plan; Codex is right to include it.
10. S14: Codex ranks it last; I reject it for now (payoff, coupling, default-bit change).
11. Ordering: Codex puts S4 fifth (before the paired and rank-3 tiles); since S1-S3 remove the
    prodsum IIFE from three of the six kernels, S4 goes after the C++ syrk tile in the merged
    order, measured on dot / stencil / recur checksums where it still applies.

---

## 5. Merged implementation order (execute item by item)

Conventions. "Ratio" = median(gfortran-expert) / median(Blade arm), as in the session summary
(> 1 = Blade faster); today's values in brackets. Every expected value is a FORECAST. "Raw-bit"
= memcmp against a matched serial reference in the same TU and mode (`-ffp-contract=off` first,
then `fast` with a `vfmadd`-count check), run by a `blade test` block beside
`cpp/linalg_probe_tests.cpp`. Every corpus test carries a route assertion (`blade plan` line) as
well as its `// EXPECT:` pins.

| # | item (sources) | effort | sites | corpus + raw-bit tests | bench cell -> expected |
|---|---|---|---|---|---|
| M0 | object/exe identity for LLVM-lane C objects; real cache-off define (S7, S12; plan 4.5a/4.1) | S | `src/Build.fs:1207-1216` (`buildLlvmShim`: key object by source+headers, clang identity, target identity, contraction, defines, sanitizer), `:1288` (`llvmExeCacheKey`: include every C object's identity), memcheck compile args (`:503/525/542`: add `-DBLADE_POOL_CACHE_OFF=1` both lanes) | build-harness test: normal then memcheck then contract=off build in ONE directory, each links a distinct object | none (enabler) |
| M1 | exact-size retained pool cache (S7; plan 4.5a; budget per section 4.4) | S | `src/cpp/nested_array_utilities.hpp:239,333,399,407,453,458`; new `blade_pool_cache.hpp` (+ deploy list `CodeGenLoopNest.fs` ~2745); `src/cpp/blade_llvm_shim.c:186-238` (64 B header with true base) | `recursive-arrays/007`, `008` unchanged pins; runtime self-test (hit/miss/evict/shared budget/thread exit/bad_alloc retry/total==0 excluded); memcheck block green with cache proven OFF | recur serial-licensed full, both lanes: llvm 117 -> 20-40 ms, ratio [0.09] -> 0.26-0.52 |
| M2 | inline canonical reads + bind-once (S5; plan 4.4a) | S | `src/CodeGenExpr.fs:803-890` (`lazyCompactRead`); helpers beside `nested_array_utilities.hpp:707` | raw-bit offset/parity test exhausting all tuples p = 0..9 (rank 2/3, sym/antisym/Hermitian, empty strict pool); corpus read tests per class incl. effectful coordinate order | comoment3 and pairkernel blade-cpp, serial-licensed AND serial-repro: comoment3 110 -> ~49 ms, ratio [0.10] -> ~0.23; pairkernel 77 -> ~42, [0.43] -> ~0.78 |
| M3 | let-rec zero-fill elision, narrow proof; drop the u0 double copy (S8 part 1, S14's recur note; plan 4.5b(i)) | S | `src/TypeCheckInfer.fs:13840` (`zerosValue`), freeze logic `:14085-14107` | recursive-arrays: lags 1/2/3 (guarded negative reads still 0), whole-prefix read and `while`-guarded recurrence KEEP the fill, `nt = 0/1` | recur, both lanes: -3..5 ms on top of M1 |
| M4 | orbit-class logical checksum, ADDITION-ONLY epilogue (S6; plan 4.4b amended) | M | recogniser beside `src/LinAlgPatterns.fs` (shared orbit-fold plan); C++ `src/CodeGenBinding.fs:3397/3425`; LLVM `src/EmitLlvm.fs:4653/4786` | raw-bit vs a scalar model of the CLASS tree (not R_serial); integer-valued corpus fixtures (exact, association-invariant pins) at rank 2, 3, strict; seeded p = 0; unseeded p = 0 (BL8003); strict p = 1 / r = 2 and p = 2 / r = 3 (+0); negative list (odd f on antisym, complex square, other array, extra coordinate use, non-zero inner seed, one unlicensed level); repro test proving the logical walk is kept | comoment3 llvm 41.5 -> ~27, ratio [0.28] -> ~0.43; pairkernel llvm 42.6 -> 34-37, [0.77] -> 0.89-0.96; pairkernel cpp (after M2) -> 34-37 |
| M5 | packed SYRK tile, C++ lane (S1; plan 4.1) | M | `src/cpp/blade_tiles.h` (pure core) + driver in `src/cpp/blade_packed_gemm.hpp`; gate-off arm of `tryGenSyrkDispatch` `src/CodeGenLoopNest.fs:2546`; `packedTriWorth` beside `src/CodeGenExprSupport.fs:1755` | raw-bit helper tests: p = 0..9, every p mod 24, MC/NC edges, n = 0..17, KC-1/KC/KC+1, S = 0/1, both contraction modes, signed zeros/cancellation/subnormals/Inf; spill check (no stack traffic in the s loop) both modes; corpus gram routes: tile (61,257) (61,513) (97,257) (97,513) (29,1001), decline (29,513) (61,31) (13,31) | gram blade-cpp serial-licensed: 275 -> 85-100 ms, ratio [0.30] -> 0.82-0.96; serial-repro gram: 1219 -> ~the licensed time |
| M6 | LLVM driver over the same core (S12; plan 4.1 twin) | M | new `src/cpp/blade_llvm_tiles.c` (serial driver, atexit scratch); `src/EmitLlvm.fs` outer-product builder ~4330-4410 (`Contract` field) and `materialize` :4047; truthful declaration attributes; needs M0 | raw-bit integration through the REAL clang object with emitter-chosen operands/scales; emitted-call assertion; `blade test llvm` and `test llvm blocks`; same-directory flag switches | gram blade-llvm serial-licensed: 157 -> 80-100 ms, ratio [0.52] -> 0.82-1.0 |
| M7 | antisym paired strict tile: sequential 6x8 default, 4x4 measured alternative (S2; plan 4.1b) | M | `(|RowContraction|_|)` beside `src/LinAlgPatterns.fs:1461` (ordered `prodsum - prodsum` match, all BlasL3 structural checks); `tryGenLinAlgNest` `src/CodeGenLoopNest.fs:2638`; LLVM via M6 | raw-bit anti family (as M5 plus multi-K-block with P and Q checked independently, every canonical cell checked, n = 0 gives +0 - +0); corpus routes: tile (61,401), multi-block (61,600), decline (23,11); `prodsum(b(1), a(0))` and `P + (-Q)` spellings decline | antisym serial-licensed (the one unsuppressed block): llvm 74.6 -> 34-42 ms, ratio [0.42] -> 0.74-0.91; cpp 109 -> 34-42 |
| M8 | comoment3 simplex tile with W panel (S3; plan 4.2) | M-L | `blade_tiles.h` sym3 core; `(|RowContraction|_|)` Sym3 shape; C++ dispatch; LLVM via M6 | raw-bit sym3 family: every equality stratum, i-sub-simplex boundaries, p = 0..9, n near KC, Scale None/Div/Mul incl. divisor 0; corpus routes against `packedSym3Worth`: tile (29,67) (29,201) (61,11) (61,201), decline (13,11) (13,201); `prodsum(c, a, b)` declines; count executed lanes with S3's formula | comoment3 llvm (after M4) ~27 -> 13-18 ms, ratio -> 0.64-0.88; cpp similar |
| M9 | explicit vector prodsum incl. self-dot 8-chain (S4; plan 4.3) | S-M | `src/CodeGenExpr.fs:412-420` arm; helper header | raw-bit vs a scalar model of the 4x4 / 8x4 tree for n = 0..35 and 20011, same-pointer operands, unaligned starts; Float32 helper; gemv-fiber re-measure BEFORE switching | dot blade-cpp (suite kernel): 1.4-1.9x on the kernel; stencil/recur cpp checksums small |
| M10 | destination passing for let-rec rows (S8 part 2; plan 4.5b(ii)) | M | return-pool ABI in `CodeGenBinding` `genFuncBody` and `EmitLlvm`; borrowed destination | callee with two return branches; aliasing destination declines; no free of interior pointer (memcheck) | recur: removes 300 allocations + ~96 MB logical traffic; -4..8 ms |
| M11 | pointwise-body fusion (S8 part 3; plan 4.5c) | L | `BLADE_FUSION` pass `src/Optimize.fs` (~:1454), gate in `optimizerGates` | `blade test opt-diff`; recur corpus; halo read splits region; `_at` RNG admitted, stateful RNG refused; raw-bit trajectory vs unfused under off | recur llvm -> 15-25 ms, ratio -> 0.42-0.70 |
| M12 | rank-1 mask compaction, C++ lane (S9) | M | `src/CodeGenBinding.fs:1869/1971/1986`; `src/cpp/index_types.h:194-195,253` | all-false / all-true / alternating / singleton / tail cases; reverse lookup round trips; missing-key BL8006; fingerprint pins | maskagg blade-cpp serial: pilot incremental ~62 -> 23-30 ms |
| M13 | strict hot-row checksum scheduling (S13; plan 4.6) | S-M | producer/consumer scheduling in `src/Optimize.fs`; `src/CodeGenLoopNest.fs:1251`; LLVM equivalent | raw-bit both fold levels under repro/off; BL8009/BL8003 preserved; minimum halo extents | stencil: -1..2 ms (13.5 -> ~12), ratio vs gf-expert [1.07] -> ~1.2 |
| M14 | SIMD inside deferred Path B chunks, one shared partition function (S10) | M | `src/CodeGenLoopNest.fs:1354-1392`, census `src/CodeGenBinding.fs:3827-3828` | raw-bit vs fixed chunk+lane tree at n = 0, 1, team-1, team, team+1; repro bitwise untouched | mt8-licensed cells (session-113651 shape): no forecast (unmeasured share) |
| M15 | producer/checksum fusion with job-order combine (S11) | M | after M4 and M5-M8: tile epilogues + `src/Optimize.fs` fold plan | one store + one contribution per valid final cell; none on partials/padding/strict diagonal; fixed-team determinism test | pairkernel -0.3..1.3 ms; mt8 removes a second pass |
| -- | S14 input sums in packing | deferred | -- | -- | -- |

---

## 6. Safe to implement first vs needs the licence or a new representation

Strict-legal and LOCAL (no association change, no new language-visible representation; each
is ON in repro and needs no licence):
- M0 build/object identity (no FP effect at all);
- M1 retained pool cache (S7);
- M2 inline canonical reads + bind-once (S5);
- M3 zero-fill elision, narrow proof (S8 part 1);
- M13 strict hot-row scheduling (S13) -- strict-legal, slightly less local (a scheduling change).

Strict-legal but NOT local (new pattern + runtime core + both lanes; ON in repro; need the
raw-bit gates first): M5, M6, M7, M8 (S1, S12, S2, S3).

Need the reassociation LICENCE (OFF under `BLADE_FP_REPRO=1`, change default-build bits):
M4 (S6), M9 (S4), M14 (S10), M15 (S11).

Need a NEW REPRESENTATION or ABI: M10 destination passing (borrowed-return ABI in both
emitters), M11 pointwise fusion (new fusion scope), M12 rank-1 compaction (new index
representation; LLVM additionally needs IRMask support), M6 (a new C object and ABI in the
LLVM lane).
