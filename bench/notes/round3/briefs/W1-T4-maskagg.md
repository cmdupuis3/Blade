# W1-4 / B-T4 -- maskagg selection structures: positions demand census (T4b) and the mask-site gather (T4a)

Read `BUILDER-PREAMBLE.md` first. Brief: `research/B-relational.md` item T4 (and its probes in `B-work/`). Review:
`reviews/codex-review-B.md` findings 8, 9, 10, 11 (the T4 rows) -- close them. Round-2 base: the B4 compaction fusion
(`Optimize.planCompactionFusion` ~2722-2925; `bench/notes/round2/brief-B-epilogues.md` B4; pins `tests/corpus/index-types/515-521`;
`blade test index-types`), which already counts in the mask pass, fuses the position fill with the gather, removes the
square pool, feeds the strict chain per 256-cell block, keeps `keep` stored and ALWAYS builds positions.

## What to build

**T4b -- positions elided when nothing demands them.** Positions are compiler metadata, not a named output (review B/8),
so eliding them does not break constraint 1. Build a DEMAND CERTIFICATE over the compound value: every consumer, alias,
returned value, callable capture, `unhash` / coordinate enumeration, present / keyed / prefix access, printing, provider
output; any unknown escape = positions REQUIRED. A null table must be unreachable from `linearize` / `unhash` / `present` /
range iteration (`src/cpp/index_types.h` `compound_index_t<1>` ~357, positions ~384, adopting ctor ~407-410 -- note the
adopting constructor currently accepts a null pointer structurally; make lookups through a null table impossible, not
merely unlikely). Keep `n` / cardinality intact (do not leave `rank_to_tuple.n = 0` meaning "no positions").
Pins 515 / 516 / 518 REQUIRE keyed source-position lookups and BL8006: their positions stay built. Add positive pins
where the census elides (values unchanged, emission needle: no positions fill) and negative pins for each demand kind.

**T4a -- the gather folded into the mask pass.** Read x once: evaluate the predicate, store the kept value and advance the
strict chain in one pass, with n+1 private capacity (logical cardinality m; distinguish the m+1 touched cells from the n
branchless stores; apply the selectivity cap `sel_cap` to static AND dynamic n with checked byte products, to every
companion and to positions). Keep B4's barriers exactly: an intervening BL8013 still fires before the later empty-fold
BL8003 (pin 521), mutation snapshots (520), the conservative intervening-mask-read / threaded-fold handling (519),
empty / tail / slack (517, 518). No value or error pin changes; update stale comments and emission needles only.

Five-party admission `selectionAdmits` (review B/11): producers audited with `scheduleMayWriteIn` (not `assignedAnywhere`),
the predicate through the shared analysis (not only the `totalScalar` whitelist), outer kernel and seed named explicitly for
the new motion (mask -> compound and companion intervals), every intervening binding; a writing party declines to the B4
form (never to the pre-B4 six-pass form unless B4 also declines).

Knob: `BLADE_COMPACTION_FUSION` gains the value `b4` (= round-2 behaviour, positions always built, no mask-site gather)
beside `0`/`off` and the default (T4); register the mirror in `optimizerGates` so `blade test opt-diff` compares b4 vs
default as well as off vs on (review B build order 2); CLAUDE.md row updated; `blade plan` records `selection: positions
elided (census: ...)` / `required by <consumer>` and `gather: at the mask site` / `declined -- <reason>`.

T4c (shared selection index across compounds over one mask) is WAVE 2 -- design it in your report but do not build it.

## Tests and blocks

index-types (515-521 unchanged in value; new 522+ for T4a/T4b positives and each demand kind), sql, loops, optimize
(decision cases for the census and the gather), cli, corpus-wiring, interp index-types / sql, opt-diff index-types, llvm
index-types. `blade plan bench/kernels/maskagg/maskagg.blade` in both regimes: positions elided (maskagg's `y` has no keyed
read -- verify) and the gather at the mask site. One `blade run` A/B of maskagg at full size, serial licensed and repro,
old vs new interleaved (directional). Report logical traffic before / after (the brief claims 30 -> 17 MB licensed,
26 -> 13 MB repro) from the emitted loops, not from timing.
