# W2-3 / D-P3 -- a row-owned PULL scatter at rank 3 (strict mt8), measured against the scatter precedence of W2-0a

Read `BUILDER-PREAMBLE.md` first (reset to the integration tip it names). Brief: `research/D-orbit-regions.md` piece P3 (and
its admission section 5). Review: `reviews/codex-review-D.md` findings 3, 4 (the P3 bullet), 6 (knob contract) and 7 (the
ownership proof and what is NOT shared) -- close them. Round-2 base: the ordered orbit scatter (`src/OrbitFold.fs` planStrict
~698; `src/cpp/blade_orbit_scatter.hpp` scatter2 / scatter3 with the same-Z third event and the retained packed H;
`src/CodeGenBinding.fs` genOrbitScatterCpp ~4120 / scatterPlan ~4237; `src/EmitLlvm.fs` ~6581 / ~6621; `blade test
orbit-scatter`; `bench/notes/round2/brief-A-ordered-scatter.md`). The wave-1 A/B (`bench/notes/round3/w1-ab.md`) measured the
SERIAL scatter beating the G35 row slots under mt8 repro on comoment3 (3.74 vs 4.43 ms, expert 1.303 / 1.120) -- that is the
baseline P3 must beat.

## The form

Rank 3, strict (every level repro), a stored SymIdx<3, p> pool: each thread OWNS whole H rows (owner a <= b: it seeds
H[a,b] once, PULLS T(c,a,b) for c < a in ascending c, then runs the THIRD=false slice on its own rows), so every pair chain
receives its source sequence from its own seed exactly as the serial scatter does; a mandatory team-completion barrier
precedes the scatter2 tail over the retained H (the middle and outer chains serial from their seeds). What is NOT preserved:
the same-Z third-event reuse across owners (f is evaluated more than once per canonical cell: 848,046 slice cells +
C(172,3) = 833,340 pulls at p = 171 vs 2,514,726 leaf applications in the serial scatter -- the gain is fewer reads and address
computations than G35's 5.0 M canonicalizing reads, not fewer f evaluations; say so in the record). Identity: bitwise the
serial scatter / the walk under REPRO + CONTRACT=off, in BOTH lanes (the LLVM lane keeps its serial scatter; the C++ pull form
is taken only when the build emits threads and the omp request is present at the exact G35 request / depth gate).

## Admission (constraint 4, review D/4)

The pool is stored and no producer or binding is moved: still audit the source use and the complete scheduled body with the
shared analysis (producer = the pool's producer, consumer = the walk's cell function, outer kernels = every level's fold kernel,
every seed, the intervening bindings between the pool's materialization and the fold -- `intervalPayloadOf`), the closed
purity whitelist of planStrict, and the request gate; unknown callbacks decline; `BLADE_ORBIT_PULL` (unset/1 = on, 0 = off) in
`suiteClearedKnobs`; CLAUDE.md row; `blade plan` record with the five-party matrix and the ownership plan (rows per thread,
pulls per row).

## Tests

`blade test orbit-scatter`: pull-on vs pull-off vs serial scatter vs the walk, BIT-PATTERN identity (not NaN-equivalence or
printed text: compare raw bits of every H cell and the total) under REPRO + CONTRACT=off, at T = 1, 3, 8 observed, with
all-equal / two-equal / distinct triples, ragged tails (p = 13, 23, 171 smoke), non-commutative kernels and nonzero / signed-zero
seeds; a route assertion (the pull form's needle). omp-coverage: the team observed. Corpus symmetry/ (next free numbers):
value pins with the knob on / off. Blocks: orbit-scatter, symmetry, omp-reduce, omp-coverage, optimize, cli, corpus-wiring,
interp symmetry, llvm symmetry, opt-diff symmetry. Directional measurement: comoment3 full size, repro, T=8, pull vs
`BLADE_FOLD_ROW_SLOTS=0` (the serial scatter) vs default, interleaved 5 rounds -- report; the harness A/B is the orchestrator's.
Gap row G66 (the measured form) at the end; update G44's note.
