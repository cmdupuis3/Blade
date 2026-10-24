# Round-3 wave-4 builder briefs (condensed)

The wave-4 launch preamble and the four gap-cleanup item briefs as handed to the builders.

Sections (each is one former scratchpad file, verbatim): [`wave4-builder-briefs.md#w4-launch-preamble`](#w4-launch-preamble), [`wave4-builder-briefs.md#w4-1-deferred-tuples-aliases`](#w4-1-deferred-tuples-aliases), [`wave4-builder-briefs.md#w4-2-fnbody-folds-refusals`](#w4-2-fnbody-folds-refusals), [`wave4-builder-briefs.md#w4-3-strict-seeds-provenance`](#w4-3-strict-seeds-provenance), [`wave4-builder-briefs.md#w4-4-grad-zero-fill`](#w4-4-grad-zero-fill)

---

## W4-LAUNCH-PREAMBLE

_formerly the scratchpad's `round3/briefs/wave4-builder-briefs.md#w4-launch-preamble` (appended at the wave-4 records commit)_

## Wave-4 builder launch preamble (pasted into each builder's prompt; <TIP> filled at launch)

You are a wave-4 builder of round 3 of the Blade microkernel work (gap-register cleanup so round 4 can start fresh). Your
worktree is a fresh checkout cut from master; FIRST run `git reset --hard <TIP>` (the integration tip on branch
worktree-bench-fortran) and verify `git log --oneline -1` shows it. Read, in order: the repository's CLAUDE.md; section 1 of
`bench/notes/round3/briefs/wave1-builder-briefs.md` (the builder preamble: one squashed commit on your branch, never push, never
rebase, the record obligations, the report format); section 0 of `bench/notes/round3/briefs/wave3-builder-briefs.md` if it
exists at the tip, else the scratchpad's `W3-PREAMBLE-ADDENDUM.md` (the wave-3 rules: every claim scoped to what ran; a
mechanism change in a fix round gets another Codex look; records say exactly which lanes and witnesses); then YOUR brief
(the path in this prompt). Light testing is the user's standing instruction: run the blocks your brief names, not the full
suite; the orchestrator runs the five gates and the harness A/B at the wave end.

Rules that bit builders this round: (1) build with `dotnet build Blade.fsproj -c Release` and run tests from YOUR worktree
root with ucrt64 g++ first on PATH (`C:\msys64\ucrt64\bin`); never run two `blade test` processes from one working directory.
(2) The git index is LF; your editor must not flip a file's stored endings (`git ls-files --eol <path>`). (3) `tests/corpus/**`
and `examples/**` are byte-pinned: never reformat them. (4) A new corpus directory needs the Corpus Wiring facts; a new
diagnostic code needs its five touch points (`src/Diagnostics.fs`, the emit site, the corpus pin grammar, docs, CLAUDE.md
where a user-visible rule changes). (5) Every lane divergence you fix lands in ALL lanes (C++, LLVM, interpreter) or is refused
in all lanes with one diagnostic -- never two behaviours for one program; a contract change is stated in `docs/formalism.md`
terms and pinned by value AND first failure. (6) Do not edit code owned by the wave-3 items (`planEquiJoin`,
`planGroupAggregate`, `planOrbitActions`, the A7c epilogue-legs hook) or `src/cpp/blade_orbit_scatter.hpp`'s scatter / pull /
action kernels beyond what your brief names; if a fix needs it, STOP and report. (7) Do not edit CLAUDE.md except where your
brief says a row or a user-visible rule changes; the orchestrator merges. (8) Report when done, in under 80 lines: the sha, what
each lane does per reproducer before / after, the contracts you stated, the pins, the block footers verbatim, what remains
unproved; the final commit must be the ONLY commit on your branch above <TIP> (squash your WIP). If your context is running
low, commit a WIP first and say so.

---

## W4-1-deferred-tuples-aliases

_formerly the scratchpad's `round3/briefs/wave4-builder-briefs.md#w4-1-deferred-tuples-aliases` (appended at the wave-4 records commit)_

## W4-1 -- deferred values in tuples and aliases: one contract, three lanes (gap rows G57, G58, G59, G60, G61, G62 + one unrowed divergence)

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and the wave-3 addendum (section
0 of `bench/notes/round3/briefs/wave3-builder-briefs.md`, or `W3-PREAMBLE-ADDENDUM.md` in the scratchpad) first. Your worktree
is cut from the integration tip named in your launch message: `git reset --hard <tip>` before anything. LIGHT testing (the
user's call): the blocks named below, not the full suite. One squashed commit; a Codex diff review and its fix round follow.

### The contract: the gap rows, verbatim

Read rows **G57, G58, G59, G60, G61, G62** in `bench/notes/gap-register.md` (W1-3 found them; the W1-3 Codex review is
`bench/notes/round3/reviews/wave1-codex-reviews.md#codex-diff-w1-3`, finding 2 for G61's first-failure consequence). The W1-3
scratch reproducers named in the rows (`z4c_wrapped_call_arg.blade`, `z2_nested_deferred_tuple.blade`, `z3_if_tuple.blade`,
`pt1_plain_tuple_order.blade`) are GONE: re-derive each from the row's text and land it as a corpus test. The seventh item is
unrowed, found by W3-1: a TOP-LEVEL deferred map that a function's `group_by` forces is PRINTED by the interpreter and NOT by the
compiled lanes (W3-1 dropped it from sql-group-by/072; it is the same alias / forcing contract as G60). Give it a row (G75) with
its reproducer.

### Deliverables (each lane divergence is fixed in ALL lanes or refused in ALL lanes -- never two behaviours for one program)

1. **G57** -- a WRAPPED unforced array as a module-level call argument, `f(m1 |> compute, 2.0)`, is forced exactly as the bare
   `f(m1)` is (`CodeGenFusion.note`'s direct-IRVar test is bypassed by the IRCompute wrapper). Both lanes run it; pin the value.
2. **G58** -- a deferred TUPLE as a tuple member, `let p = (m1, m2); let q = (p, 1.0)`: extend W1-3's member-ordered tuple path
   (`explicitComputeTupleMember` / `bareDeferredTupleMember` / `tupleNeedsMemberOrder`) to tuple-typed members, descending into a
   named deferred tuple. Pin.
3. **G59** -- a tuple of computes inside an `if`: route IRIf-held tuples through the ordered path in BOTH lanes (today C++ BL9002,
   LLVM refuses). Pin in both lanes, both branches, and the first failure when a branch member fails.
4. **G62** -- a plain tuple of two FAILING scalar members with no compute member reports BL8013 in C++ (g++ evaluates
   `std::make_tuple` arguments right to left) and BL8006 in LLVM / the interpreter (left to right). Fix: bind evaluating scalar
   members to temporaries in SOURCE order whenever more than one member can fail -- only when a member is not a plain variable /
   literal, so ordinary scalar tuples keep their emission (pin that: a two-literal tuple's C++ is byte-identical before / after).
   Pin the first failure (code + location) in all three lanes.
5. **G60 + G75 (the alias / forcing contract)** -- `let g = method_for(x) <@> ..; let g2 = g` prints `g` in C++ and nothing in the
   interpreter (`genVarAliasBinding` forces the source before copying; `Interp Core.evalBinding` returns a deferred alias before
   its copy branch); a top-level deferred map forced inside a function is printed by the interpreter and not by the compiled
   lanes. Decide ONE contract -- which bindings of a deferred map are in the print list and when the map is forced -- state it in
   `docs/formalism.md` (section 10.2, the deferral rules; say what changes), implement it in C++, LLVM and the interpreter, and
   pin the printed set and the values. The recommended reading: a deferred map is forced AT MOST ONCE, at its first forcing use;
   an ALIAS of it is the same deferred value (not a copy) and is printed iff the alias binding itself is a printed top-level
   binding whose value is forced by the end of the program; a never-forced deferred binding prints nothing in every lane. If you
   choose otherwise, say why.
6. **G61 (the tuple-member contract)** -- a BARE deferred array held in a tuple, `(g, 1.0)`: three behaviours today (LLVM eager,
   interpreter deferred with a later projection forcing it, C++ refuses `unevaluated computation used as value`, tuples/041). ONE
   contract in all three lanes. The recommended choice: FORCE AT TUPLE CONSTRUCTION (a tuple is a value; its members are values;
   the LLVM lane's behaviour), stated in formalism 10.2 terms WITH its first-failure consequence named (W1-3 finding 2: a failing
   member that the as-written program never projected now fails at the construction). Re-pin tuples/041 from a `(rejects)`
   refusal to a value + first-failure pin; add the rank-1 and rank-2 cases and a failing-member case. If you choose the deferred
   reading instead, the interpreter is the reference and both compiled lanes must defer; say why.

### Tests and blocks

Corpus: new files in `tests/corpus/tuples/` (next free: check `ls`), `deferred/` or `printing/` (whichever holds the alias /
print contract today -- check), one per reproducer, pins = value AND `// ABORT:` / `// ERROR:` first failure where a member
fails; `blade test tuples`, `deferred` / `printing` (the dirs you touch), `cli`, `docs` (formalism fences), `corpus-wiring`,
`interp tuples` (+ the other dirs), `llvm tuples` (+), `opt-diff tuples` (+). Every corpus file you add must run in the LLVM
lane or carry a recorded lane refusal -- a divergence fixed in two lanes and skipped in the third is not fixed.

### Records

Gap rows G57-G62 -> `FIXED (round 3 wave 4, W4-1)` with the pins named; G75 added and fixed; `docs/formalism.md` 10.2 for the
two contracts (G60 / G75 and G61); `docs/features.md` tuples / deferral rows; CLAUDE.md ONLY if a user-visible rule changes (the
tuple-construction forcing is one: one sentence in the language-essentials bullets). Report as the addendum says: shas, the two
contracts as stated, every lane's behaviour per reproducer before / after, footers, what remains unproved.

### Scope

Not: performance, the sibling-fold / epilogue machinery, the W3 items' code. If a fix needs a change in a wave-3 planner
(`planEquiJoin`, `planGroupAggregate`, `planOrbitActions`), stop and report instead.

---

## W4-2-fnbody-folds-refusals

_formerly the scratchpad's `round3/briefs/wave4-builder-briefs.md#w4-2-fnbody-folds-refusals` (appended at the wave-4 records commit)_

## W4-2 -- function-body folds, captures and refusals (gap rows G54, G55, G64, G67)

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and the wave-3 addendum (section
0 of `bench/notes/round3/briefs/wave3-builder-briefs.md`, or `W3-PREAMBLE-ADDENDUM.md` in the scratchpad) first. Your worktree
is cut from the integration tip named in your launch message: `git reset --hard <tip>` before anything. LIGHT testing (the
user's call): the blocks named below. One squashed commit; a Codex diff review and its fix round follow.

### The contract: the gap rows, verbatim

Read rows **G54, G55, G64, G67** in `bench/notes/gap-register.md` (W1-5 found G54 / G55: `reviews/wave1-codex-reviews.md#codex-
diff-w1-5`; W1-4 found G64: its Codex re-review of df90b999 is cited in the row; W2-5 found G67: `reviews/wave2-codex-
reviews.md#codex-diff-w2-5`). The W1-5 scratch reproducers (`leak_repro.blade`, `omp_let_repro.blade`) are GONE: re-derive them
from the rows' text and land them as corpus tests.

### Deliverables

1. **G54 (a leak)** -- a function-local named array read only through a capture in a licensed fold's map lambda is never freed
   in the C++ lane: `CodeGenLoopNest.fs` ~4389-4423 seeds the captured set from every body let, so a NON-escaping synchronous
   fold lambda's capture keeps the array alive to function exit (tensorop's 7 MB `y` inside `kernel`). Fix: an escape analysis
   that distinguishes an ESCAPING closure (stored, returned, passed on, captured by a deferred map that outlives the statement)
   from a SYNCHRONOUS local consumer (a fold / map kernel that completes within the statement, team included); the array is
   freed when that fold / team completes, not at function exit; an escaping capture keeps today's lifetime. Pin the free in the
   `blade test memfree` style (the ASan census of `cell-epilogue` / `memfree-stress` shows the pattern) and the value; the
   earlier sibling `t` (freed today) stays freed. Both lanes: say what the LLVM lane does today for the same program and pin it.
2. **G55 (a wrong refusal)** -- `let s = reduce(x, lambda(p, q) where omp -> p + q, 0.0); s` in a FUNCTION body over a stored
   rank-1 array is declined "reduce in expression position ... bind the fold to its own let" although it IS its own let;
   `axes = 1` takes the other route and threads. `CodeGen.fs` ~523-543 routes only ARRAY-valued IRReduce lets through statement
   emission; a scalar let reaches `CodeGenExpr.fs` ~1534-1536's expression-only decline. Fix: dispatch scalar function-body
   fold lets through the statement fold path (the team, the census line `[omp] ...`, the K-lane / Path B forms exactly as at
   top level); pin the team (`omp-coverage` style), the census line, the value, and the byte identity of the function-body
   form with the top-level spelling under REPRO; correct the diagnostic's suggestion for the cases that remain refused.
3. **G64 (an ICE)** -- a whole-array kernel applied over a compound, `object_for(at0) <@> y |> compute` with
   `function at0(c: Array<Float64 like CompoundIdx<keep>>) -> Float64 = c(2)` over `y = compound(x, keep)`, is BL9002 (`'__v15'
   was not declared`): `CodeGenLoopNest.fs` ~1520 initializes the kernel-parameter map that is filled only while peeling loop
   levels (~2064-2095); a whole-array kernel has ZERO S-dimension levels to peel, so its whole-operand parameter is never
   bound. Fix: bind the whole-operand parameter when there are no levels to peel (the kernel sees the compound as its
   argument, keyed reads `c(2)` through the compound's index -- which the compaction census already keeps for this shape:
   W1-4 finding 6 says positions are REQUIRED here; assert that the plan does not elide them) -- or, if the shape has no
   sound meaning, refuse it with ONE diagnostic (a BL4xxx, same text in all three lanes), never BL9002. Pin whichever you do
   in all lanes (the interpreter's current behaviour first: does it run it?).
4. **G67 (a crash in all three lanes)** -- a ROW MAP whose cell is a packed `comm` contraction (a per-group SymIdx H:
   `let h = method_for(range<G>) <@> lambda(g) -> group_h(jt(g)) |> compute` with `function group_h(jg) = method_for(jg, jg)
   <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb)`) checks but does not run: C++ `BL8005 std::bad_array_new_length`,
   LLVM refuses (`a compact output whose group extents disagree with the loop levels`) and falls back to the same C++ failure,
   the interpreter panics BL8003 `coordinate/shape mismatch`. Read the row's cause column (the grouped output's rank-3 SymIdx
   declaration). Decide: if the FULL fix (a correctly declared grouped compact output: rank 1 over G of packed SymIdx<2, P>
   rows, with the storage, the printing and the differentials) fits the item, build it in all three lanes with pins; if it is a
   feature (the N10 `groupls` prerequisite), land the CLEAN REFUSAL -- one diagnostic at typecheck or lowering, the same in every
   lane, naming the unsupported shape and the spelling that works (a flat `method_for(range<G>, ...)` over a decompacted row, or
   the shape W2-5 used for `haloadjoint`) -- and record the feature as NOT BUILT with its design in the row. Never a run-time
   crash, never three different failures.

### Tests and blocks

Corpus: `functions/`, `omp/` or `omp-reduce/` (G55), `compaction/` or `index-types/` (G64), `symmetry/` (G67) -- next free
numbers, check `ls`; pins = value + `// ABORT:` / `// ERROR:` where a refusal is the fix. Blocks: `blade test functions`,
`omp-reduce`, `omp-coverage` (G55's team), `cell-epilogue` or `memfree-stress` (G54's ASan census -- whichever hosts the
memfree pattern; say which), `compaction`, `symmetry`, `cli`, `docs`, `corpus-wiring`, and the `interp` / `llvm` / `opt-diff`
differentials on every dir you touch.

### Records

Gap rows G54 / G55 / G64 / G67 -> `FIXED` (or `REFUSED CLEANLY, feature NOT BUILT` for G67 / G64) with the pins named; the
diagnostic codes you add follow the five touch points (`src/Diagnostics.fs`, the corpus pin, docs); CLAUDE.md only if a
user-visible rule changes (G55's "a scalar fold let in a function body threads like the top-level spelling" is one sentence
in the `where omp` bullet if you land it). Report as the addendum says.

### Scope

Not: performance work, the W3 planners, the epilogue machinery. A fix that needs a change in `Optimize.fs`'s wave-3 code: stop
and report.

---

## W4-3-strict-seeds-provenance

_formerly the scratchpad's `round3/briefs/wave4-builder-briefs.md#w4-3-strict-seeds-provenance` (appended at the wave-4 records commit)_

## W4-3 -- strict seeds and failure provenance: the unseeded head peel in every strict form (G74), the LLVM inlined-body panic location

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and the wave-3 addendum (section
0 of `bench/notes/round3/briefs/wave3-builder-briefs.md`, or `W3-PREAMBLE-ADDENDUM.md` in the scratchpad) first. Your worktree
is cut from the integration tip named in your launch message: `git reset --hard <tip>` before anything. LIGHT testing (the
user's call): the blocks named below. One squashed commit; a Codex diff review and its fix round follow. BOTH items change
the bits a strict program prints or the text a failure prints: every change lands in ALL lanes at once, with the old and new
behaviour pinned raw-bit / verbatim.

### Item 1: G74 -- an UNSEEDED deferred-row fold is the exact head-peeled chain in EVERY strict form

The facts (W3-3's README observations, `bench/notes/round3/prototypes/p1-commit/README.md` section "Observations"; W3-4's
Codex review finding 2, `reviews/wave3-codex-reviews.md#codex-diff-w3-4` once condensed, today the scratchpad's
`codex-diff-W3-4.md`): the checker SYNTHESIZES a `+0.0` seed for a seedless deferred `(+)` (`TypeCheckInfer.fs` ~2841-2860),
`Lowering.fs` ~974-982 carries it into `IRReduceCompute`, and every strict form then starts its chain at `+0.0` -- the walk's
row callable (`double __ret = 0.0;` in pairkernel's emitted C++), the ordered scatter's row-chain seed (`double(0.0)`), the
row-owned pull, the row action (`action2`), the LLVM lane's forms, AND the interpreter -- while the SAME fold spelled over a
stored row view keeps the head peel (`row[0]` copied, the chain from the second cell) and BL8003 on an empty row. Consequence:
an all-(-0.0) row prints `+0.0` under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` where the exact chain gives `-0.0`; the lanes
agree with each other today only because they share the synthesized seed. Formalism 2.4 says reduce is the LEFT fold of the
cells; an unseeded fold's first value is the first cell, not an identity.

Deliverable: carry UNSEEDED through the shared fold representation (no synthesized seed at the checker: an `IRReduceCompute`
without a seed, or a seed marker the forms interpret as "copy the first term"), and make every strict form head-peel: the
walk's row callable, `OrbitFold.planStrict`'s row chains (G-row groups: the first term of each chain copied, in the scatter's
own order), the pull's leaf chains, the action's chains (W3-4's `aseedless` / `bseedless` witness flips from `+0.0` to `-0.0`
BY DESIGN -- update it and say so), the LLVM emissions of each, the interpreter. Keep BL8003 on a statically empty row (the
literal range extent makes it unreachable here, but the check stays where the source has it). LICENSED forms are untouched
(their signed zero is the licence's, G40). Pins: raw bits (`BLADE_PRINT_BITS`) in `blade test orbit-scatter` (walk, scatter,
pull, action, both lanes, G in {1, 3, 4, 8}) and `omp-reduce` for all-(-0.0) rows and for a mixed row whose first cell is a
NaN payload (the first cell's bits must survive as the head); the interpreter's decimal text; corpus `symmetry/` and
`omp-reduce/` files with the `-0.0` pins. The `test interp` and `opt-diff` differentials on symmetry and omp-reduce.

### Item 2: the LLVM lane's missing location for a guard panic inside an inlined function body

The fact (W3-1's report, out of its scope): a guard panic inside an inlined function body (an empty unseeded reduce in a peel)
prints no `--> file:line` in the LLVM lane when the call is a destructuring `let (a, b) = f(..)`; the C++ lane prints the
function's line. W3-1's Codex review (finding 4) required that the T3 plan NOT fix this inside the item: "if improving missing
spans, change ordinary and planned paths together in a separately identified diagnostic fix". This is that fix.

Deliverable: the ORDINARY LLVM path prints the same location and frame the C++ lane prints for a guard panic (BL8003, BL8006,
BL8013, BL8014) raised inside an inlined function body reached through a destructuring let -- the function's own span, as
for a plain `let x = f(..)`; then every PLANNED path (T3's peels and gk, if merged by then; A7c's inlined spine) follows the
ordinary one. Pin the complete failure text (code, `--> file:line`, the frame line) in both lanes for the destructuring and
the plain forms, and the ORDER when two guards could fire. Corpus: `diagnostics/` or `functions/` (check where the location
pins live: `// ABORT:` with the location substring); `blade test llvm` on those dirs; `cli` if a fixture is there.

### Blocks

`blade test orbit-scatter`, `omp-reduce`, `symmetry`, `fold-lanes` (licensed forms unchanged: a control), `cli`, `docs`,
`corpus-wiring`, `interp symmetry`, `interp omp-reduce`, `llvm symmetry`, `llvm tiles` (the LLVM scatter core), `opt-diff
symmetry`, and the diagnostics dir's blocks for item 2.

### Records

Gap row G74 (add it: the facts above, FIXED with the pins named; G40 stays as the licensed contract); G73's seedless sentence
and W3-4's `aseedless` comment updated; `docs/formalism.md` 2.4 / 7.x if a sentence about unseeded folds needs stating (it
should already imply this: say so or fix it); the LLVM location fix recorded in the gap register as its own row (G76) ->
FIXED. CLAUDE.md: the `BLADE_ORBIT_SCATTER` / `BLADE_ORBIT_PULL` / `BLADE_ORBIT_ACTIONS` rows' signed-zero sentences, if any
claim `+0.0` for a seedless row. Report as the addendum says: the forms changed, the before / after bits, footers.

### Scope

Not: licensed forms, performance, the T3 / T2 / P2 planners' own code (item 2 touches only the ordinary LLVM emission; the
planned paths follow by construction -- if one does not, report it rather than patch it).

---

## W4-4-grad-zero-fill

_formerly the scratchpad's `round3/briefs/wave4-builder-briefs.md#w4-4-grad-zero-fill` (appended at the wave-4 records commit)_

## W4-4 -- reverse mode's cotangent zero buffer as a ZERO FILL, not an n-element array literal (gap row G68)

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and the wave-3 addendum (section
0 of `bench/notes/round3/briefs/wave3-builder-briefs.md`, or `W3-PREAMBLE-ADDENDUM.md` in the scratchpad) first. Your worktree
is cut from the integration tip named in your launch message: `git reset --hard <tip>` before anything. LIGHT testing (the
user's call): the blocks named below. One squashed commit; a Codex diff review and its fix round follow.

### The contract: gap row G68, verbatim

Read row **G68** in `bench/notes/gap-register.md` (W2-5 found it: `reviews/wave2-codex-reviews.md#codex-diff-w2-5`). The
facts: `Grad.fs` ~397-403 (C6: a combinator-built diff local gets `zerosOfDims ds`) and `GradExpand.zerosOfDims` ~1541-1544
build the cotangent's zero buffer as `ExprArrayLit (List.replicate n ..)` -- an n-element ARRAY LITERAL -- so the C++ lane
emits `static const double __vN__init[n - 4] = { 0.0, ... }` + `std::copy_n`, and compile time is super-linear in the map's
extent (`BLADE_PHASE_TIMING=1 blade emit` on `bench/kernels/haloadjoint`: `[phase] lower` 19 s at n = 50,021, 95 s at
100,003; emit 3 / 6 / 24 / 97 s at 5003 / 20011 / 50021 / 100003; at 1,000,003 `blade compile` was killed after 627 CPU s).
The kernel's full size is capped at n = 50,021 because of it; the catalog says 1,000,003.

### Deliverable

The zero buffer is a ZERO FILL: an IR form that every lane lowers to a fill (C++: a `std::fill_n` / a zero-initializing
constructor over the allocated pool; LLVM: a `memset` / `zeroinitializer` store; the interpreter: a filled array), never an
n-element literal -- the same form the language already uses for a zero array where one exists (check `zeros`-like builtins,
`let rec`'s pre-allocated buffer, the `method_for(range<I>) <@> lambda(_) -> 0.0 |> compute` lowering) and reuse it rather
than inventing one. Value identical in every lane (`test interp` and `test llvm` on `ad/`, `grad/` -- whichever dirs hold the
reverse-mode corpus -- and the haloadjoint kernel's checksum pins in `bench/validation`). Compile time linear in n: record
`[phase] lower` / `emit` before and after at n = 5003 / 20011 / 50021 / 100003 / 1,000,003 (the last one must now finish;
say its time), one core, in the gap row. Then RAISE haloadjoint's full size to the catalog's 1,000,003 in
`bench/kernels/haloadjoint/kernel.json` + the spec and regenerate its pins (`bench/validation`: the ordered oracle at the new
n), so the wave's end chain measures it at the intended size; keep the smoke size.

### Blocks

`blade test ad` (or the AD block's key: check `blade` usage), `grad`, `cli`, `docs`, `corpus-wiring`, `interp <ad dir>`,
`llvm <ad dir>`, `opt-diff <ad dir>`; `bench/harness/run.py check haloadjoint --regimes all` at the new size (smoke evidence
for the chain).

### Records

G68 -> `FIXED` with the timings; the haloadjoint row in `bench/notes/kernel-list.md` and its spec / kernel.json at the catalog
size; `docs/features.md` AD row if it states a size limit. No CLAUDE.md change expected. Report as the addendum says.

### Scope

Not: any other AD change, the halo gather / scatter rule (`BLADE_AD_HALO_GATHER`), performance of the generated program
(a fill is not slower than a copy from a literal; say so with one number if you measure it, but do not tune).
