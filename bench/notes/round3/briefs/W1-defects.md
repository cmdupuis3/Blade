# W1-3 -- deferred-materialization defects in the C++ lane (four reproducers, BL9002 today)

Read `BUILDER-PREAMBLE.md` first. These are correctness fixes, no performance claim. Each reproducer stays its OWN corpus
pin and its own gap-register line; do not merge their identities unless you trace them to one site (review B/12, D/9).

1. **D-B1** `compound(<unforced inline map>, mask(...))`: the emitted C++ reads `matched__dense` which is never declared
   (`scratchpad/round3/research/B-work/p2.cpp.txt:227,255`; the forced control `p2b.cpp.txt:228-238` declares and fills
   it). Cause per review B/12: the computed-operand path `CodeGenBinding.fs ~2161-2170` calls `genBinding` and DISCARDS its
   updated context, whereas the `IRVar` arm (~2157-2159) forces. Fix: force and propagate the resulting binding / context.
2. **D-B2** `unique` over a rank-1 compound emits `.extents` / `[]` which `Compound` lacks
   (`B-work/p1.cpp.txt:292-301`; `src/cpp/nested_array_types.hpp:142-166` has neither). The interpreter already reaches
   `compoundToDense` through `forceInputArray` / `resolveArraySource` (`src/Interp/Loops.fs ~2530, ~797, ~1828`): a lane
   divergence. Fix: use the compact cardinality and data access in both `unique` passes.
3. **Fixture 02 (brief D)**: a tuple of computes `(h |> compute, y |> compute)` returned from a block passes undeclared
   `__v53 / __v60` to `std::make_tuple` (`scratchpad/round3/research/D-work/emit02.txt:345-348`): each tuple member's
   compute must be materialized and bound in the emitting context, recursively forcing the compact sources it needs while
   keeping the required stores.
4. **RESULTS 15.4 card**: "an unforced map over a compact pool fails BL9002 in the C++ lane" -- the probe is in
   `bench/notes/round2/codex-diff-W2F-3.md` finding 13 (a shape-preserving map over a compact `comm` producer, read by a
   nested walk; the LLVM lane handles it). Also the G41 row's "deferred map captured in a nested kernel: 'g' was not
   declared". Fix the forcing site; if 3 and 4 turn out to be the same site, say so with the evidence.

For each: a corpus pin that RUNS in both lanes with the interpreter's values (`blade test interp <dir>` on the new files),
plus a pure control. Blocks: index-types, sql (the discovered union), loops, symmetry, orbit-scatter, functions, tuples,
cli, corpus-wiring, interp on the touched directories, llvm on the touched directories, opt-diff on the touched
directories. Close the RESULTS 15.4 card text and the G41 note; add a gap row only for anything you could not fix.
