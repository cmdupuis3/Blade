# W2-1 / B-T1 -- membership hoisting (`contains` over a small set), both lanes

Read `BUILDER-PREAMBLE.md` first (reset to the integration tip it names). Brief: `research/B-relational.md` item T1 and its
probes in `research/B-work/`. Review: `reviews/codex-review-B.md` finding 7 (HIGH) and the T1 lines of finding 11 -- close them.
Round-2/3 context: the write analysis (`Optimize.scheduleMayWriteIn`, `callbackTable`, `intervalPayloadOf`) is the admission
predicate for every party; the compaction census of W1-4 (`Optimize.compactionRoots`, `positionsDemand`) shows the shape of a
module-wide traversal.

## Today

`contains` is a linear scan rendered as an IIFE with the TARGET expression interpolated INSIDE the scan (src/CodeGenExpr.fs
~572-588); a mask predicate calling a named function keeps the call by name (~2121-2124), so the old "set hoist" was a no-op
(the `SubstMap` for contains is unwired: the "NOT YET WIRED UP" note is in CodeGenLoopNest.fs ~37-40; SubstMap has other live
uses -- do not remove it). The LLVM lane has NO `IRContains` arm (the whole program falls back to C++, confirmed by
`B-work/p1.llvm.err.txt`). The interpreter evaluates the target ONCE even for an empty set (src/Interp/Loops.fs ~2487-2508);
the C++ IIFE evaluates it per scanned element -- a pre-existing evaluation-count divergence for a non-trivial target.

## What to build

1. One evaluation contract for the target: evaluate the target expression exactly once, before the scan, in every lane
   (the interpreter's behaviour) -- including an empty set, and with the target's own failure order kept (a failing target
   fails before any element is read). Pin it (a counting / failing target over an empty and a non-empty set, both lanes +
   interpreter).
2. Membership hoisting: when the set operand is a stored array (or a named deferred array the write analysis proves
   effect-free and total), build ONE search structure before the HOST binding (the mask / map / fold that contains the probe)
   and probe it per cell. Chooser: linear / bitmap (span = max - min + 1 within a bound, M >= some minimum) / hash, decided
   deterministically from M, min, max and the knob only -- never from timing. Identical boolean results in all three
   representations (duplicates, empty set (handle M = 0 before min/max), negative and extreme keys, overflow-checked span
   arithmetic; occupancy independent of key values); forced representations fall back safely outside their domain. Integer
   keys only in v1 (Int64; EnumIdx keys if cheap). Knob `BLADE_MEMBER_HOIST` (unset/1 = on, 0 = off, `linear|bitmap|hash` =
   force), registered in `optimizerGates` (the hoist moves the set build before the host: `blade test opt-diff` compares off
   vs on), CLAUDE.md row, `blade plan` record `[member-hoist v1] <host>: applied (bitmap, span 1009, M 53) / declined -- <why>`.
3. The LLVM lane: a real `IRContains` arm (the linear form at least; the hoisted structure shared through the same runtime
   helper as the C++ lane if the helper lives in src/cpp as a C-callable core, as the tiles do) so a `contains` program no
   longer falls back; `blade test llvm` on the new corpus files must run the LLVM lane proper (assert the lane, not "0 failed").
4. Admission `membershipAdmits` (constraint 4): the set producer (stored, or a proved-pure deferred value forced at the hoist
   site, evaluated once -- say so), the enclosing fold kernel and seed, every statement inside the host and the argument /
   seed evaluation before its cell loop, every parallel body affected (the hoisted build itself is serial); a writing party
   declines to the as-written per-cell scan. Five-party matrix in the plan record and the gap row.

## Tests

corpus sql-unique-contains (next free numbers): small / large / empty / negative / duplicate key sets x linear / bitmap / hash
(forced) with identical pins; the target-evaluation-count pins; a writing set producer declines; a probe inside a `where omp`
map (the hoisted structure is read-only shared); interp and llvm on the directory; optimize decision cases; cli needles for
the three representations. Blocks: sql, index-types, loops, optimize, cli, corpus-wiring, interp sql-unique-contains /
sql-masks, llvm sql-unique-contains, opt-diff sql-unique-contains. Kernel: `semiagg` is wave 3's (needs T4c); for now add the
brief's `semiagg` SOURCE as a corpus-sized probe only if cheap. Gap row for T1 (next free number: G65) with the matrix.
