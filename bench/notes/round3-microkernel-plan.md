# Round 3 -- plan (staged waves), 2026-10-20/21

Inputs: four research briefs (`research/A-census.md`, `B-relational.md`, `C-workloads.md`, `D-orbit-regions.md`) and their
Codex attack reviews (`reviews/codex-review-{A,B,C,D}.md`). Baseline tree: the combined line after round 2 + wave 3 + the
effect-analysis follow-ups (worktree-bench-fortran, to become master once its gates pass). Process as in round 2: builder ->
Codex diff review -> merge -> targeted blocks -> wave-end five gates -> harness A/B with knob-off mirrors
(`bench/notes/round2/w2-scripts/`). Roster: four opus/high builders, two Codex reviewers, fable for arbitration, one
sonnet runner. EVERY new scheduling admission consults `Optimize.scheduleMayWriteIn` / `callbackTable` for the producer
kernel, the consumer kernel, the outer fold kernel, the seed and the intervening bindings (five-party matrix in the plan
record of each pass), and keeps the as-written failure order (first-failure equivalence).

## 0. Decisions the user owns (asked at wrap-up)

- **A4 retention policy (wave-3 G52).** Blade frees a kernel's large pool (recur 48 MB, stencil 24 MB) inside its timed
  region; the Fortran experts deallocate after their timer. Keeping blocks until thread exit moves that cost out of the
  timer (recur mt8 11.5 -> 8.0 ms in wave 3's probe) but changes the peak working set the harness records. Options:
  (a) keep the current policy and record the asymmetry; (b) add an opt-in retention arm, measured and reported beside
  the default; (c) make retention the default. Codex (review A, finding 6): never a silent default change; measure as
  its own arm first. Recommendation: (b).
- **Benchmark output contract for new kernels.** Review B/10 and C/1: only outputs the spec names are stored in every arm;
  user-named top-level arrays that the spec does not list must either be listed or spelled as compiler temporaries. Each
  new kernel spec names its outputs before any timing. (No decision needed unless the user wants the stricter "every
  top-level let is an output" rule.)

## 1. Record fixes before anything merges (orchestrator, no code)

- Gap numbering: wave 3's G48/G49/G50 were renumbered **G51/G52/G53** at the rebase (the effect-analysis branch keeps
  G48-G50). Update any later reference.
- G37 row: the fix landed (963b9aa1, ancestor of master); set FIXED.
- Wave-3 numbers: the quiet re-run populates RESULTS 16.3 (done by wave 3 at 570ad3ef); the contaminated first run stays
  labelled.
- Census corrections (review A/8-9): blueprint rank 7 (bounded-key grouping) is PARTIAL (positional / enum / dynamic
  dispatch exists; aggregate-only execution and region selection are the missing parts); B2l is NOT BUILT / no
  detectable gain, not REFUTED.

## 2. Wave 1 -- measure first, small admissions, correctness, two workloads

| item | what | who | admission / gate | A/B cell |
|---|---|---|---|---|
| **W1-1 A1 experiment** | mt8 repro with `BLADE_FOLD_ROW_SLOTS=0` (ordered scatter instead of G35 row slots), pairkernel + comoment3, interleaved vs default, `BLADE_FP_CONTRACT=off`, assert the selected form, compare raw bits, total and consumer phase separately; then a precedence choice per kernel (keep row slots where they win) | runner | no code; if the scatter wins, a one-line precedence change + plan record follows as W1-1b | pairkernel / comoment3 mt8 repro (0.687 / 1.097 today) |
| **W1-2 A2 (G51)** | the G35 row-slot and Path B forms admit a row-VIEW inner fold (`reduce(u(i), op, s)`) in REPRO: narrow admission (stored dominating dense rank-2 base, row contiguous, inner fold complete and total, both seeds preserved and evaluated once, BL8003 for the unseeded empty row kept, no cross-binding motion) -- review A/4-5: never drop a seed, never call the reassociating `blade_fold::sum` on a strict inner fold; the LICENSED helper arm (sum<double,8> instead of `omp simd` around 8 scalar chains) is a separate, gated, measured arm | builder | `rowViewFoldAdmission` five-party matrix; corpus pins original vs nested-range spelling bitwise in repro; omp-reduce cases | stencil serial + mt8 repro input checksum (2.7-2.9 ms today) |
| **W1-3 deferred-materialization defects** | D-B1 `compound(<unforced inline map>, mask)` reads an undeclared `matched__dense` (CodeGenBinding ~2161-2170 discards the forced context); D-B2 `unique` over a rank-1 compound emits `.extents` / `[]`; fixture-02 (brief D) tuple-of-computes with undeclared `__v53/__v60`; the RESULTS 15.4 "unforced map over a compact pool" BL9002 -- fix each, keep separate reproducers (review B/12, D/9: do not merge their identities without evidence), interpreter twin pins | builder | correctness only; corpus pins both lanes; `blade test interp` on the new files | none (BL9002 -> runs) |
| **W1-4 T4b + T4a (maskagg)** | T4b: positions table elided when a complete demand census (every consumer, alias, escape, print, provider; unknown = required) finds no keyed read -- positions are compiler metadata, not a named output; T4a: gather folded into the mask pass with n+1 capacity (logical cardinality m) -- keep B4's failure barriers (BL8013 before BL8003, keyed BL8006), `keep` and every named companion stored; pins index-types 515-521 unchanged in value | builder | extends `planCompactionFusion`; knob `BLADE_COMPACTION_FUSION=b4` mirror in optimizerGates; demand certificate; five-party matrix | maskagg serial licensed / repro (0.788 / 0.813 vs ifx) |
| **W1-5 workloads N07 + N09** | N07 as the honest "resident two-output FIR / sibling-map fusion" (not ghost reuse): y1 and y2 stored, separate +0 prodsum chains, inputonly twin, natural = two slice statements, expert = fused direct loops; N09 tensorop: outputs X/A/B/C and Y only (T1/T2 are the compiler's), contraction order I then J then K preserved, baseline = the existing `m.mode_product` / unfold-matmul spelling, ordered scalar oracle for strict pins, natural + expert twins | builder | kernel.json / spec.md / natural.f90 / expert.f90 / pins; smoke; no compiler change | new cells |
| **W1-6 anti_g re-measure** | quiet session, anti_g=1 vs 0 (auto), antisym serial + mt8, 3 interleaved sessions each; flip the default only on live blocks beyond the CI (wave 3: auto 1.3-1.4% slower serially, live; round 2: 0.954 mt8 indicative) | runner | none | antisym |

Wave-1 end: targeted blocks + five gates + A/B (cells above, knob-off mirrors). RESULTS section 17.1.

## 3. Wave 2 -- relational T1 / T4c, D/P3, producer-input legs, N10

| item | what | admission |
|---|---|---|
| **W2-1 T1 membership hoisting** | build the set once before the host binding; deterministic chooser (linear / bitmap / hash from M, min, max and knobs only -- never timing); identical integer equality in all three (duplicates, empty, negative / extreme keys, M=0 before min/max, overflow-checked spans); a real LLVM `IRContains` arm; the target's evaluation count and failure order kept as the interpreter has them (the C++ IIFE interpolates the target inside the scan; the interpreter evaluates it once even for an empty set -- normalize both lanes to one contract, or restrict hoisting to proven total invariant targets); knob `BLADE_MEMBER_HOIST` | `membershipAdmits`: producer / source forcing, enclosing fold kernel and seed, statements inside the host, every parallel body affected |
| **W2-2 T4c shared selection** | one index for all compounds over the same mask, freed exactly once, each companion's data stored; the pure/total membership-probe certificate after T1 | extends W1-4 |
| **W2-3 D/P3 row-owned pull scatter, rank 3** | each thread owns whole H rows (owner a <= b: seed H[a,b] once, pull T(c,a,b) for c < a ascending, then the THIRD=false slice); mandatory team completion before the scatter2 tail over retained H; no same-Z third-event reuse across owners (f evaluated once per canonical cell is NOT preserved -- the gain is reads/addressing); bit-pattern identity tests across C++ on/off, G35, scatter, LLVM, interpreter under REPRO+off; measured against W1-1's winner | the exact G35 request/depth gate; producer stored; five-party matrix; `BLADE_ORBIT_PULL` in suiteClearedKnobs |
| **W2-4 A7c producer-input legs (saxpy)** | input sums ride the y2 producer pass as extra legs (B1's epilogue machinery): closed totality, dominating captures / seeds, operand / index equality, alias checks, complete intervening audit; each leg keeps its own seed and licence; strict legs never threaded | `producerEpilogueLegAdmission` |
| **W2-5 workload N10 groupls** | conditional on a `blade emit` probe of the per-group packed H returned from a row map (RESULTS 15.4-adjacent shape); outputs cost / gradient / packed H only; residual and derivative producers spelled with the highest deferred idiom (no forced tapes); +0 seeds kept; logical-H checksum weighting matched by the expert; rectangular groups, hand-derived derivatives declared | if the shape refuses: open the gap, advance corrected N02 (1-D, explicit ordered update list for the adjoint, both twins) instead |

Wave-2 end: gates + A/B. RESULTS 17.2.

## 4. Wave 3 -- T3 redesigned, T2 at pilot size, D/P1 prototype

| item | what |
|---|---|
| **W3-1 T3 aggregate-only grouping (REDESIGN per review B)** | v1 accepts only already-stored, dominating, shape-compatible value sources and total, dominating seeds; separate state machines for seeded / unseeded / prodsum / count (unseeded: first value consumed exactly once; seeded: the exact chain from the seed incl. empty groups); positional, then EnumIdx (DECLARED order, negative members valid), then dynamic (first-occurrence) regimes kept apart; BL8006 key validation order and span kept, each BL8003 at its original peel; the licensed per-group form is a legal association change (not within 1e-9) -- strict first, licensed only after its own numerical + performance evidence; a group fold writing a captured counter DECLINES; named grouped arrays never become null rows; knob `BLADE_GROUP_AGG` |
| **W3-2 T2 hash equijoin** | stable forward-filled CSR, left-major / right-minor probe (pin the 2x2 duplicate case: values [2,3,20,30]), output sizing / growth or a counting pass (J unknown beforehand), bounded key distribution so J fits the 64 MB rule, `matched` stored; pilot size first, C++ with an honest LLVM fallback status; knob `BLADE_HASH_JOIN` |
| **W3-3 D/P1 ticket-ordered producer-fused commit (strict mt8, rank 2)** | standalone costed PROTOTYPE first (review D/1-2): ticket = an explicit canonical row-group interval (G-aligned claims, not thread ids or readiness), production of every row the interval reads finished before commit, ONE outer accumulator advanced through completed groups in increasing i (no per-group subtotals, no reseed); acquire/release on the ticket; measure per-group production / commit, total wait in core-time, longest stall, actual team, G in {1,4,8}, T in {1,3,8}; planner/emitter integration only if paired end-to-end evidence beats W1-1's winner; C++ only with the LLVM lane keeping its serial scatter (bit-identity tests across forms) |
| **W3-4 D/P2 rank-2 actions (pairaction)** | stored rank-2 actions, single channel first; fair expert twins (dspmv-style serial, output-owned mt8), packed named W/T/H stores and z/u/y outputs with a checksum that reads every stored element; the Deduce rule for permuted reads of compact arrays is SEPARATE work with its own proof (symmetric read = identity transport; antisymmetric = permutation sign; Hermitian = conjugation; never lifts to the whole cell term) |

Wave-3 end: gates + A/B. RESULTS 17.3.

## 5. Deferred / dropped (with the reason)

- A4 retention policy: user decision (section 0). A5 in-team recur row folds and A6 ordered-commit pipeline for global
  strict chains: measure-first / research only (review A/7, verdict A6 DROP this round). A7a generator fusion across
  functions and A7b 32x32 multiplies: no admission design / unproven range proof -- instrument only (input-only twins).
- N01 (needs halo boundary modes and rolling let-rec storage: `claude/recursive-array-materialization-381708` unmerged),
  N03 (one specified online algorithm; LLVM tuple-state fold), N04 (honest banded natural twin; sentinel rules), N05
  (two-phase expert; LLVM has no LU nodes), N06 (canonical signed-pair contract; its 6x cost premise unsupported -- named
  deferred arrays are forced by name), N08 (rolling storage; LLVM has no IRRandAt arm; Philox port): language or
  backend work first, tracked as gap rows / cards.
- D/P2 contraction tower, rank-4 partial symmetry, packed-coordinate differentiation: after P2 rank-2 lands.
- B2l: not built, no detectable gain in the instrument. D1 persistent syrk team: dropped on the instrument (wave 3).

## 6. Builder briefs (to write per wave, from the briefs + reviews)

Each builder prompt = the item row above + the brief section + the Codex findings it must close + the preamble's hard
constraints + the block list to run + "ONE commit per fix round, Fable trailer, no push". Reviewers get the diff and the
findings list; verdict BUILD / MERGE AFTER FIXES / DO NOT MERGE.

## 7. Execution log

- 2026-10-21 ~02:00 -- WAVE 1 MERGED (squashed per item): W1-5 workloads 6459275b, W1-3 defects 982a984e, W1-A2 row-view
  63710ee2, W1-4 maskagg 9da403a8 (= the wave-1 tip). Each item had a Codex diff review (W1-4 two rounds; the T4a gather
  dropped to opt-in on the brief's own drop line); the hidden side-table writer hole was found live in three OTHER planners
  (hot-row, cell epilogue, compact row classes: 13 vs 90, 4 vs 84, 12 vs 24) and closed by `Optimize.intervalPayloadOf`.
  Corpus numbers collided at merge (W1-4's loops/528-530 -> 537-539). Gap rows G54-G64.
- 2026-10-21 02:25-06:37 -- WAVE-1 END CHAIN unattended: gates green on 9da403a8 (7389 / 2173 / 131 / 841 / 3077, 0 failed),
  mirrors r3old (master 66e04875 binary) / r3new / r3noslots / r3gather / r3antig0 smoked, 23 A/B sessions, 0 failures
  (`round3/w1-ab.md`): stencil mt8 repro 0.876 (expert 0.805/0.877 -> 0.924/0.959); maskagg serial licensed 0.817 llvm /
  0.772 cpp (expert 1.064/0.777 -> 1.290/0.970), repro 0.866 / 0.856 (1.073/0.827 -> 1.260/0.955); T4a on top 0.974-0.985
  (stays opt-in); A1: the scatter beats the row slots under mt8 repro on pairkernel 0.841 and comoment3 0.843 (expert
  0.686/0.653 -> 0.795/0.805 and 1.107/0.945 -> 1.303/1.120) -> W2-0 flips the precedence; anti_g auto 1-2% slower serially,
  4.8% faster under threads -> W2-0b per-regime default; twofir 0.58-0.75 under threads (G56 target), tensorop 0.14-0.19 serial
  / 0.05 mt8 (the recognizer target); gram control flat; the one CI-edge "slower" (pairkernel serial repro cpp 1.004) is noise on
  an untouched program. Codex merge-level review: no lost hunk; two record fixes applied.

### Wave 2 (2026-10-21, launched 07:10; DRAFT -- completed at the wave end)

Integration branch `worktree-bench-fortran`, base 667250cd (the wave-1 records tip). One squashed commit per item, each
after its Codex diff review (`bench/notes/round3/reviews/codex-diff-W2-*.md`) and the fix round it demanded; merge-level
block runs on the integration tree after each code merge (`gates/w2-gates-summary.md`).

| item | branch commit(s) | review verdict -> fix round | integration commit | merge-tree blocks |
|---|---|---|---|---|
| W2-5 haloadjoint (N02; N10 gated out as G67, G68 opened) | 91d13a00 -> 67955f70 | MERGE AFTER FIXES (5: mt8 metadata, repro committed, source-named auxiliaries, transcendental counts, payload size) -> landed | e4dec0ba | bench only |
| W2-0 scatter precedence under mt8 repro + anti_g per regime (team cap) | e6d55563 -> f3b3d4e3 | MERGE AFTER FIXES (4: raw-bit pins, producer teams, anti_g keyed on a team, records) -> landed | e66b179c | with W2-3: 15 blocks green (`w2-merge`) |
| W2-3 row-owned pull scatter (rank 3), executed form recorded, BLADE_PRINT_BITS | 5bd469f7 -> 8f2d2480 | MERGE AFTER FIXES (1 HIGH: form chosen on the REQUESTED team, unrecorded; 2 MEDIUM) -> landed | 844e2ae7 | as above |
| W2-1 membership hoisting (T1) | 0bc965eb -> 0a37e6c4 -> 04a60dfd (NEW 1-3, on the tip) | DO NOT MERGE (BLOCKER: interpreter compound snapshot; 3 HIGH) -> fix round -> second look MERGE AFTER FIXES (originals resolved; NEW 1 host-placement pin, NEW 2 refusal tests, NEW 3 run-record `forms` in every record = W2-3 code) -> round 2 landed: host-anchored C++ routes (31 rows = the corpus exactly; a 4-mutation self-test), strict refusal rows, `forms` only when recorded + 5 parsed-JSON fixtures | e2194721 + 04a60dfd (fast-forward) | 22 blocks green incl. run-record 34/0 (`w2-merge-2`); round 2's blocks on its own tree: sql 188/0, index-types 390/0, optimize 410/0, cli 165/0, run-record 39/0, orbit-scatter 30/0, llvm facts 104/0 + sql-* , interp sql-*, docs, corpus-wiring |
| W2-4 A7c producer-input legs (saxpy) | c0b927a3 (on e4dec0ba) -> 62ba1a49 (replayed onto e2194721) -> 9f2877a7 (fixes 1-5) | MERGE AFTER FIXES (3 HIGH: dominance through seed callables, the inlining-lifetime confounder, route-less failure fixture; 2 MEDIUM: inline committed before the emitters, licence premise) -> landed: transitive FreeVarsOf dominance + fresh-storage operand rule (028); the PREFERRED lifetime route (the inlined callee's pools freed at the call's original return point, `freeAllocNow`; ASan counts identical gate on/off); 026/029 pinned by route in both lanes; the planner mirrors both lanes' fold gates before keeping the inline; licence wording. Lifetime-matched: C++ repro 16.37 -> 14.99 ms, LLVM repro 11.66 -> 10.37 (both clear the 0.8 ms drop line), C++ licensed 15.98 -> 15.41 (does not). Second look of the fix round (`wave2-codex-reviews.md#codex-diff-w2-4-2`): DO NOT MERGE on the cleanup machinery AFTER the squash had landed -- NEW 1 (BLOCKER) the join can sit inside the inlined callee before its last pool use (a live pool freed; the cleanup boundary was inferred from the join, not the call's return); NEW 2 (BLOCKER) two admitted producers in one call release the same pools twice in main. Fix round 2 (70234a4a: `InlineReturnTable`, one release per inlined call at the scalar-return let; witnesses 030 / 031; ASan equal) merged as 3a3925ac. THIRD look (`wave2-codex-reviews.md#codex-diff-w2-4-3`): DO NOT MERGE -- NEW 2 resolved, NEW 1 partial; four holes of the class remain: the join rewrite can delete the boundary key; CSE after the table can merge a caller map into the freed pool / drop the key; function-body callers bypass the dispatch arm; the LLVM lane DID free the callee's pools at return, so the inline retains them (LLVM saxpy attribution WITHDRAWN). Round 2 merged as strictly better than the join-inferred release; ROUND 4 (brief `briefs/wave2-builder-briefs.md#w2-4-round4-lifetime`: an IR-level cleanup event honoured by every pass, every renderer and both lanes, fail closed elsewhere) runs AFTER the wave-2 A/B (builder load contaminates timing), then the five gates re-run on the final tip and the saxpy cells are re-measured | 58efa62d + 3a3925ac | 15 blocks green on 58efa62d (`w2-merge-3`); round 2's blocks on its branch green |
| W2-2 T4c shared selection (+ membership certificate) | 278353cb (on e2194721) -> move to 58efa62d + fix round pending | MERGE AFTER FIXES: its "BLOCKER 1" (singleton emission changed) RULED intended -- Part B's semiagg is a single compound; legacy singletons over the mask source must stay byte-identical (pinned), the two new singleton shapes get route pins; HIGH 2 producer storage not proved through aliases (reuse `storageOf`, fail closed on a deferred source, never force a future companion early); HIGH 3 runtime / shared-route coverage overstated (542 route-less; LLVM runtime guard edges unexercised; ownership = comments, no executed free at top level) -> fix round landed (550e26d2 replay onto 58efa62d with W2-1's route table extended for 537-539; b93f37cf fixes): legacy singletons pinned byte-identical (32/32 emissions vs the 58efa62d build), the new singletons route-pinned with twins, `storedArrayProof` shared with membership and applied to every source, C++ fail-closed on a deferred source, 542 on the shared route, dyn2 route-asserted, the LLVM run-time shared arm DECLINED (not built), lifetime checks relabelled | merged after W2-6 | blocks on the branch: index-types 401/0, optimize 486/0, cli 192/0, opt-diff index-types 316/0, interp index-types 399/0, llvm index-types 183/0/218, sql, docs 132/0 |
| W2-6 A4 pool-retention arm (user decision 2026-10-21: opt-in `BLADE_POOL_RETAIN`) | 46dec984 (on 844e2ae7) -> d7ff8880 (replayed onto 04a60dfd) -> 8273b00d (fixes 1-5) | MERGE AFTER FIXES (1 HIGH: the LLVM lane's process-wide budget recorded as per-thread; 3 MEDIUM: spoiled timings in G52, tests unsetting the knob past the suite pin, `policy.pool` additive contract; 1 LOW: provenance suffix on the default line) -> landed; the builder's T=8 directional attempt was spoiled by load and is STRUCK (wave-3 probe numbers stand, probe-only); measured by the wave-2 A/B mirror r3w2retain with peak working set | merged after W2-4 | its blocks on the branch: alloc 3x73/73, run-record 58/0, cli 175/0, llvm shim 12/0, facts 104/0, docs, corpus-wiring, sql, orbit-scatter |

Decisions settled in wave 2 so far:
- The user ruled on A4: an opt-in retention arm measured beside the default (time AND peak working set), never a default change.
- Precedence hierarchy under mt8 repro: pull (rank 3) > ordered scatter > G35 row slots; `BLADE_FOLD_ROW_SLOTS=prefer` restores the
  old order below the pull, `BLADE_ORBIT_PULL=0` removes the pull. Bit equality of the forms is PINNED (raw bits), not inferred from timings.
- `contains` has ONE target-evaluation contract in every lane (set first, target once, before any cell, empty set included).
- Inlining is a measurement confounder (W2-4): a kept one-level inline extends callee-local lifetimes, moving a scope-exit free out
  of the timed region; the fix round either restores the cleanup boundary or discloses it on every record.

Merge mechanics: squash merges collided only additively (both sides adding at the same place); the resolver scripts
(`gates/resolve-w2-3.py`, `resolve-w2-1.py`) keep both sides, order gap rows by number, and re-apply the other side's edits to a
both-changed row by base-context anchor (G44: W2-0 rewrote the tail where W2-3 appended -- hand-merged and corrected). Gap numbers:
G65 T1, G66 pull, G67/G68 (W2-5), G69 A7c, G70 T4c; G52 updated by W2-6.

Wave-2 end (pending): five gates on the final tip, examples sweep, mirrors r3w1 (the wave-1 binary) / r3w2 / r3w2prefer / r3w2pull0 /
r3w2antig1 / r3w2legs0 / r3w2retain, smoke, interleaved A/B (pairkernel + comoment3 mt8 repro; antisym x3 sessions; saxpy 4 regimes;
haloadjoint 4 regimes; stencil + recur mt8 with peak working set; gram control), `analyze-w2.py` -> RESULTS 17.2, gap rows MEASURED.
| W2-4 rounds 4-8 (A7c lifetime class) | 19898911 (round 4: IRReleasePools event) -> 23fe111c (round 5: fail-closed whitelist) -> 5c230f74 (round 6: allocation-free evaluation) -> ec57d7d3 (round 7: no user call in a non-owned unit) -> 1d294fbd (round 8: the kept inline OPT-IN, the user's fallback) | looks 4-7 all DO NOT MERGE (alias copy unowned; start removable; type vs evaluation; tuple-position expansion; a fold kernel named by a non-owned unit is never audited) -> `reviews/wave2-codex-reviews.md#codex-diff-w2-4-7` | squash on 972633c6 (the wave-3 integration tip) | item blocks only (user: light testing); the five gates + the saxpy re-measure run in the wave-3 chain |

### Wave 3 (launched 2026-10-21 from 361504b6 = master after the wave 1-2 push; closed 2026-10-22)

| item | integration commit | looks | outcome |
|---|---|---|---|
| W3-3 D/P1 producer-fused ordered commit (prototype) | 3641bfc1 | the salvage note `reviews/wave3-codex-reviews.md#codex-w3-3-salvage` | MEASURED and DROPPED by the predeclared rule (bimodal T8); one cheap affinity experiment named, not taken |
| W3-4 D/P2 row orbit actions (G73) | 972633c6 | one (MERGE AFTER FIXES) | `BLADE_ORBIT_ACTIONS`; measured 17.8: serial 0.45-0.59 of the walk |
| W2-4 A7c lifetime class (G69), rounds 4-9 | aa39e50a, bea9fcfe | seven diff looks + the G69 design note | the kept inline OPT-IN by the user's decision; BL7005 when retained; model A = a later item |
| W3-2 T2 hash equijoin, pilot (G72) | 4f3a37b6 | two | `BLADE_HASH_JOIN`; measured 17.8: 100x over the six-pass form at pilot size, still 0.78-0.89 of the experts |
| W3-1 T3 aggregate-only grouping (G71) | 5ea03056 + records e0991a39 | three | `BLADE_GROUP_AGG`; measured 17.8: 0.44-0.49 under REPRO, groupagg_dyn 0.05 llvm |

### Wave 4 -- the gap-register cleanup (planned 2026-10-22; launched from 5ea03056; closed 2026-10-24 under the fallback rule)

The user's direction: the correctness / lane-divergence / refusal / compile-time rows this round, bandwidth and peak memory (G36,
G37, G38, G56) for a fresh round 4. Items (the full brief texts: `round3/briefs/wave4-builder-briefs.md`):

| Item | Gap rows | Area | Builder's deliverable |
|---|---|---|---|
| **W4-1 Deferred values in tuples and aliases** | G57, G58, G59, G60, G61, G62 + W3-1's noticed interpreter-vs-compiled PRINT divergence (a top-level deferred map that a function's `group_by` forces is printed by the interpreter and not by the compiled lanes) | `CodeGenFusion` / `CodeGenBinding` tuple and alias paths (the member-ordered tuple path of W1-3), `Interp/Core.evalBinding`, `EmitLlvm` tuple handling, `docs/formalism.md` 10.2 | G57: force a WRAPPED unforced argument `f(m1 \|> compute, 2.0)` as the bare one is. G58: extend the member-ordered tuple path to tuple-typed members. G59: route IRIf-held tuples through the ordered path in BOTH lanes. G62: bind evaluating scalar members to temporaries in source order whenever more than one member can fail (only when a member is not a plain variable / literal), so C++ reports the FIRST failing member like the interpreter and LLVM. G60 + the print divergence: ONE alias / forcing contract for a deferred map held by name (what is printed, when it is forced) implemented in all three lanes. G61: ONE contract for a bare deferred array held in a tuple -- the recommended choice is FORCE AT TUPLE CONSTRUCTION (the LLVM lane's behaviour; a tuple is a value), stated in formalism 10.2 terms with the first-failure consequence named; implemented in all three lanes; tuples/041 re-pinned from a refusal to a value + first-failure pin. Corpus: tuples/, deferred/, printing; interp + llvm differentials on those dirs. |
| **W4-2 Function-body folds, captures and refusals** | G54, G55, G64, G67 | `CodeGenLoopNest` (~4389-4423 captured set; ~1520 / ~2064-2099 kernel-parameter map), `CodeGen.fs` ~523-543 fold-let routing, `CodeGenExpr` ~1534, the grouped SymIdx output declaration (all three lanes) | G54: escape analysis -- a function-local array read only through a NON-escaping synchronous fold lambda is freed when the fold / team completes, not at function exit (pin the free, memfree style; the ASan census). G55: dispatch a SCALAR function-body fold let `let s = reduce(x, lambda(p, q) where omp -> p + q, 0.0)` through the statement fold path (team + census line), and correct the diagnostic's suggestion. G64: bind a whole-array kernel's operand parameter when there are zero levels to peel (`object_for(at0) <@> y \|> compute` over a compound) -- fix, or refuse with a diagnostic instead of BL9002. G67: a row map whose cell is a packed `comm` contraction (a per-group SymIdx H) -- declare the grouped compact output correctly in all three lanes (C++ bad_array_new_length, LLVM refusal, interpreter BL8003 today); if the full fix is a feature, land the CLEAN REFUSAL (one diagnostic, same in all lanes) and record the feature as the N10 `groupls` prerequisite. |
| **W4-3 Strict seeds and failure provenance** | G74 candidate (W3-3 / W3-4), W3-1's noticed LLVM location divergence | `OrbitFold` / the walk's row callable and the scatter's row-chain seed, the action's `action2` seeding, `TypeCheckInfer` ~2841-2860 (the checker's +0.0 seed for a seedless deferred `(+)`), `Lowering.fs` ~974-982, `EmitLlvm` inlined-body panic locations | G74: an UNSEEDED `reduce(<deferred row map>, (+))` must be the exact head-peeled chain (first value copied, consumed once) in EVERY strict form -- the walk's row callable (`double __ret = 0.0;`), the scatter's row-chain seed (`double(0.0)`), the action -- and in the interpreter; an all-(-0.0) row then prints -0.0 under REPRO; the shared fold representation carries "unseeded" instead of a synthesized +0.0 seed (TypeCheckInfer / Lowering), all twins change together; BL8003 on a statically empty row preserved; raw-bit pins (BLADE_PRINT_BITS) in `blade test orbit-scatter` (the `aseedless` witness flips to -0.0 by design) and `omp-reduce`. LLVM location: a guard panic inside an inlined function body under a destructuring `let (a, b) = f(..)` prints `--> file:line` of the function's line as the C++ lane does -- in the ORDINARY path, and the planned paths follow (this is the "separately identified diagnostic fix" W3-1's review asked for; it must not be folded into T3). |
| **W4-4 Compile-time debt** | G68 | `Grad.fs` ~397-403, `GradExpand.zerosOfDims` ~1541-1544, the C++ zero-fill emission | the cotangent zero buffer of a combinator-built local is a ZERO FILL (a `std::fill_n` / a memset-able constructor, the LLVM lane's zeroinitializer), never an n-element array literal; compile time linear in n; haloadjoint's full size (n = 1,000,003) compiles; `BLADE_PHASE_TIMING` figures before / after in the record; value identical (interp + llvm differentials on ad/ and the haloadjoint kernel). |
| **W4-5 (G69 -- decided by the design note `reviews/codex-g69-design.md`: CLOSE VIA A)** | G69 | the inlined region gets REAL lifetime scopes in both lanes (C++ lexical + allocation scope; an LLVM pool scope opened before the first evaluated unit and closed after the evaluated return, scalar results exported), AND every repeated kernel / tuple expansion (`applyKernel`, `emitTupleParts`) gets its own per-invocation scope; CSE / reuse / IRLift barriers at both ends; the seven looks' counterexamples + scope cases as the regression matrix; LLVM runtime allocation accounting | 10-15 builder-days: NOT a wave-4 item (too large for a cleanup wave) -- the user's call whether it opens round 4 (an ownership model peakmem work needs anyway) or waits. MEANWHILE (USER DECIDED 2026-10-22): the opt-in arm STAYS and gets a compiler WARNING (stable code, both lanes) whenever the kept inline is actually retained -- W2-4 round 9; model A is a later item, not wave 4. |
| **W4-6 (P1 -- decided by the salvage note `reviews/codex-w3-3-salvage.md`: NEEDS ONE CHEAP EXPERIMENT FIRST)** | the P1 commit (G35 / G44 lines) | `bench/notes/round3/prototypes/p1-commit` (the standalone harness only; no compiler change) | ONE experiment, 0.5-1 builder-day, in a quiet window: topology-verified per-worker PHYSICAL-CORE affinity for the existing G = 4 schedule and its controls (native Windows affinity per worker, one logical CPU per physical core, the master bound too, API success and entry / exit CPU ids checked; the existing P1_AFFINITY process mask is not it), applied identically to a / a2:4 / b:4 / d:4 / di:4; two independently shuffled 21-round blocks each holding bound AND unbound arms; CPU-id and timestamp logging in di only; G = 4 predeclared. DECIDER (predeclared): bound d:4 vs bound b:4 paired LOWER 95% CI >= 1.5 ms AND median wait fraction < 0.20, reproduced in the second block; failure = P1 stops under this target (the fixed-P bound leaves no margin: median bound + outside = 6.87 ms vs the 6.88 ms needed); success = an OPT-IN bound configuration, which does not overturn the DROP for the unbound harness. The expert Fortran is compared under the same affinity before any parity claim. Also fix the README's first-touch attribution (pool faults lie inside production time). |

Not in wave 4 (round 4, bandwidth / peakmem): G36, G37, G38, G56; the N-workloads needing language work (N01 / N03 / N04 / N05 /
N06 / N08); the items' v1.1 lists (T2 LLVM arm / conjunctive keys / fold-into-probe / full size; T3 means and tuple joins,
group_bucket; P2 multi-channel, scalar form, rank-3 stage, momenttower).

Sequencing (approved 2026-10-22): the four builders cut from the tip after W3-1 / W3-2 merge; ONE combined end chain after wave 4
(the wave-4 items have no benched cell of their own). Fallback (the user, 2026-10-22 / 23): a third look that still fails merges
what passes and records every remaining hole as an OPEN gap row; then "don't continue any more rounds on these. Wrap up what's
working for wave 4, then benchmark, and we'll do more work there later."

Execution log:

- Launched from 5ea03056 (the W3-1 merge) as approved: W4-1, W4-2, W4-3, W4-4; Codex diff review per item; fix rounds as the looks demanded.
- W4-4: one look (MERGE AFTER FIXES) + fix round; merged a7c1394c (G68 FIXED; G78 opened).
- W4-1: three looks, all DO NOT MERGE; the third found ONE regression (the interpreter forcing block-local tuples of deferred tuples) and five pre-existing gaps.
- W4-2: two looks, both DO NOT MERGE; the second found ONE regression (a use after free: the synchronous-kernel exemption kept while a scalar return's call argument stored a closure) and three record / coverage gaps.
- W4-3: two looks, both DO NOT MERGE; every finding a pre-existing gap or an evidence gap (one strict-by-construction route still +0.0; a named kernel in a repro body; the multi-module declaration table; a lambda-prefix name test; the uncommitted sweep).
- 2026-10-23: the builders were terminated by the weekly Opus limit mid round 3 (W4-1 mid final commit; W4-2 / W4-3 with WIP commits). USER: "Try again, but don't continue any more rounds on these. Wrap up what's working for wave 4, then benchmark, and we'll do more work there later."
- Orchestrator merges under that rule, no further Codex look: W4-1 = d8c58d9e + the builder's 14-line interpreter fix for its regression (tuples/066-067; 067's function-body variant dropped -- a PRE-EXISTING C++ hole, verified against the master binary, recorded in G58) -> 5d4a28d0; W4-2 = b3e0a1be + a FAIL-CLOSED rule for its regression (the exemption withheld in any scope whose scalar return names a callable; memfree/029, ASan clean) -> 2bd7b485; W4-3 = 6406fa6c + two fail-safe fixes (the declaration table attached to the final program object; the exact anonymous name `lambda`), corpus renumbered (symmetry/142 -> 145, functions/535 -> 537, 536 -> 538). The builders' WIP round-3 commits NOT taken (unreviewed). Gap rows: G57 / G59 / G60 / G54 / G64 / G74 PARTIAL with the holes stated; G76 (W4-1's provisional) renumbered G79; new G80 (named kernel in a repro body). Light checks green per item (see the commit messages).
- 2026-10-24 00:20-04:51 -- the COMBINED END CHAIN on f46fdfc0 unattended (`round3/w3-scripts/wave3-end.ps1`): gates green
  (8223 / 2364 / 133 / 950 / 3229, 0 failed), examples 68 files with the same six pre-existing failures, eight mirrors smoked,
  27 A/B sessions, none aborted, no harness failures -> `round3/w3-ab.md`, `gates/w3-gates-summary.md`, RESULTS 17.6-17.8, gap
  rows G69 / G71 / G72 / G73 MEASURED. Round 3 closes at the records commit; the master merge and push are the user's call.
