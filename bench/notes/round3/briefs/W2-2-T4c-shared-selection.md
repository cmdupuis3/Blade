# W2-2 -- T4c shared selection: one index for every compound over the same mask; the membership-probe certificate

Read `BUILDER-PREAMBLE.md` first (its gap-register range is stale: the register reaches G69 on the integration branch
when you start; this item's row is **G70**). Your worktree is cut from the integration tip named in your task (it
includes W1-4's T4b/T4a and W2-1's T1 membership hoisting). Research: `round3/research/B-relational.md` section 2 "T4c"
(lines ~227-242), the `filtergroup` / `semiagg` rows of section 3, section 4 (`CompactionPlan` gains `Companions`;
"today a second compound overwrites the entry"), section 5 row T4; attack review `round3/reviews/codex-review-B.md`
findings 8 and 9 (T4c obligations: sharing an index must free it exactly once and retain each companion's data store;
demand must cover every consumer and alias; the borrowed `totalScalar` whitelist rejects captures and `contains`, so T1
does NOT automatically make the semiagg predicate eligible -- a specific pure/total membership-probe certificate with the
same snapshot/effect proof is required). W1-4's record: gap row G63, `bench/notes/round3/reviews/codex-diff-W1-4*.md`,
corpus index-types 515-531.

## The shape

`keep = mask(x, pred)`, then J companions `y_j = compound(c_j, keep)` with every `c_j` over x's index space (the R1
`filtergroup` shape; `semiagg` = `keep = mask(cust, lambda(c) -> contains(elig, c))`, `y = compound(amt, keep)`). Today
each `y_j` builds its OWN `compound_index_t<1>` (count pass + fill pass each; `index_types.h` ~357, adopting form ~386 /
~432), and B4/T4 declines every companion whose dense operand is not the mask's source ("the mask's source is not the
compound's dense operand"). `CompactionTable.ByMask` (`Optimize.fs` ~3037-3053) holds ONE plan per mask: a second
compound over the same mask overwrites the first entry -- verify that on the tip and treat it as the first thing to fix.

## The rewrite

ONE selection index is built in the mask pass (T4b's census decides whether positions exist at all; a companion's keyed
read demands them for the shared index exactly as it would for its own), owned by the plan, with a `deallocate_compound_shared`
(or equivalent single owner) so that no companion frees it and it is freed exactly once, after the last companion's
last use (hard constraint 1: each companion's DATA store stays its own named array; only the index object is shared).
The J gathers become one pass over the selected positions (or fuse into T4a's mask-site pass under
`BLADE_COMPACTION_FUSION=gather` when J <= 4), ascending, serial: identical values, cardinality and positions, so
byte-identical in both regimes. The folds over the companions are untouched (B4a / B4c keep their chains and seeds).

Part B -- the membership-probe certificate: a mask predicate that is `contains(B, v)` (or `contains(B, v) && p(v)`,
`!contains(...)` for the antijoin) with the T1 hoist ADMITTED for that host becomes eligible for the T4 single pass and
for T4c: certify it as pure and total (the probe reads a stored, never-assigned Int64 set bound before the host, no
capture of a writer, no panic site in the target expression; the T1 planner's own admission record is the proof input,
not a re-derivation) and keep the SAME snapshot / effect proof T4 uses for `totalScalar`; a predicate T1 declined stays
on the per-probe scan and T4c declines with the reason. Never widen `totalScalar` itself.

## Admission -- `selectionAdmits` extended (the compaction-fusion row; `Optimize.planCompactionFusion` ~3178,
`selectionAdmits` ~2939 / ~3638-3694, `positionsDemand` ~3329)

- producers x and every `c_j`: `assignedAnywhere` false; `c_j` dominates the mask (bound before it) or is a stored
  binding between the mask and its compound that nothing writes.
- the predicate: `totalScalar`, or the Part B certificate.
- everything from the mask through the LAST companion passes the write + force audit (`scheduleMayWriteIn programFuncs
  modul` by payload, `intervalPayloadOf`): a `keep` assigned, or read between the mask and the last companion by anything
  but a planned compound, declines; a companion written in between declines; a planned companion whose compound is
  itself read before a later companion is built is fine (its data is its own) but its INDEX must then already be complete
  (the shared index is complete at the end of the mask pass -- state that invariant in the record).
- the outer kernel and seed as in B4; `callbackTable` for parallel bodies.
- refusals: rank >= 2 or trailing dims; a companion over a different index space (BL3016 territory: never admit);
  more than one mask; a companion that escapes its index identity (returned, captured by a callable, provider output)
  keeps T4b's demand rules.
- the five-party matrix (producers / predicate / outer kernel / seed / intervening) in the `blade plan` record and G70.

First-failure (hard constraint 5): B4's barriers stay -- an intervening BL8013 fires before a later empty-fold BL8003
(pin 521's shape), a keyed BL8006 at its original site, `keep` and every named companion stored before any later failure.

## Lanes

Both: C++ `materializeMaskFormCounted` (`CodeGenBinding.fs` ~2809; counted arm `CodeGenExpr.materializeMaskForm` ~2146),
`genCompoundInitBinding` ~2374 / `genFusedCompaction` ~2541; LLVM `emitCompoundInit` and the fused compaction
(`EmitLlvm.fs` ~7824-7971; LLVM is maskagg's reference arm). Interpreter unchanged. Identity: every pin of index-types
515-531 unchanged in VALUE; new pins bitwise equal under gate-off; `interp` on the new files; `opt-diff` index-types.

## Knob, record, docs

Rides `BLADE_COMPACTION_FUSION` (unset/1 = v2 with T4c; `b4` and `gather` mirrors keep their meanings; `0` off) --
if you need a separate escape hatch for the shared index, `BLADE_COMPACTION_FUSION=unshared` (one index per compound,
the pre-T4c v2) is acceptable and must be in `optimizerGateMirrors`. Plan record `compaction-fusion ... applied -- one
selection index shared by J companions (y1, y2, ...), freed once after <last>` / `declined -- <why>`; the certificate's
own line (`membership probe certified pure/total by member-hoist's admission` / `not certified: <why>`). CLAUDE.md row
updated; `docs/features/sql.md` section 2 (single-pass and shared selection). Gap row **G70**: mechanism, matrix, the
`sel_cap` interaction (T4a's n+1 capacity applies per companion; the shared index is one object), what is NOT built.

## Tests

- corpus `tests/corpus/index-types/532+` (next free at the tip; check `ls`): two companions sharing an index (`y1(k)`,
  `y2(k)` agree with gate-off; `interp` runs it); three companions with one keyed read (positions kept for the shared
  index; BL8006 pinned at its site); a companion over a different index space refused; `keep` read between the mask and
  the last companion declines (values unchanged); a companion written in between declines; a `contains` predicate with
  the T1 hoist admitted -> T4 single pass + shared index (the certificate line), and with T1 declined -> per-probe scan,
  T4c declined; the antijoin `!contains`; the first-failure ABORT case (intervening BL8013 before an empty-fold BL8003);
  the `gather` mirror with J = 2; `sel_cap` fallback with two companions.
- `tests/OptimizeTests.fs` decision cases per matrix row; emission needles both lanes (ONE index build, ONE free, each
  companion's data store present; gate-off / `unshared` restores one index per compound); `cli` plan needles;
  `llvm` sql-* and index-types; `interp index-types`; `opt-diff index-types`; `docs`; `corpus-wiring`; `sql`.
- Directional, one core, in-session: a `filtergroup`-shaped program (the R1 sketch minus `unique`, J = 3 companions,
  n = 1,000,003, non-power-of-two) and the `semiagg` shape (M = 1009), OLD binary vs NEW interleaved, >= 11 rounds, both
  lanes, licensed and repro. The research's DROP line: < 10% on filtergroup means record and stop.

## Not in scope

T3 grouping; T2 joins; `unique` (D-B2 is fixed; keep it out of the fixtures); the interpreter; any bench kernel source.

## Report

The commit sha (ONE squashed commit on your branch on top of the tip; both co-author lines), files touched per lane, the
matrix as recorded, the directional numbers, footers for: index-types, sql, optimize, cli, llvm (index-types, sql-*),
interp index-types, opt-diff index-types, docs, corpus-wiring. Name what you could not verify.
