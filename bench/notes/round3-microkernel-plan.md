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
