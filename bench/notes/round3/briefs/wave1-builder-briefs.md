# Round-3 wave-1 builder briefs (condensed)

The shared builder preamble (hard constraints 1-7) and the wave-1 item briefs, as handed to the builders.

Sections (each is one former file, verbatim): [`wave1-builder-briefs.md#builder-preamble`](#builder-preamble), [`wave1-builder-briefs.md#w1-a2-rowview`](#w1-a2-rowview), [`wave1-builder-briefs.md#w1-t4-maskagg`](#w1-t4-maskagg), [`wave1-builder-briefs.md#w1-defects`](#w1-defects), [`wave1-builder-briefs.md#w1-runner`](#w1-runner), [`wave1-builder-briefs.md#w1-workloads`](#w1-workloads)

---

## BUILDER-PREAMBLE

_formerly `bench/notes/round3/briefs/BUILDER-PREAMBLE.md`_

### Round 3 -- builder preamble (shared by every wave-1 item)

You are a builder in round 3 of Blade's microkernel work. Blade: an array-functional language whose F# compiler emits
C++17 (g++ ucrt64) and LLVM, with a tree-walking interpreter as a differential twin. Read `CLAUDE.md` in your worktree
first; it is authoritative on building, testing, pins and style.

### Where you work

- Your own git worktree (the orchestrator created it) cut from the integration branch `worktree-bench-fortran` (the tip
  that passed the round-2/wave-3 gates). Commit there; never push; never touch another worktree.
- Build: PowerShell tool, `C:\msys64\ucrt64\bin` FIRST on PATH, `dotnet build Blade.fsproj -c Release` in your worktree;
  the binary is `bin\Release\net10.0\Blade.exe`. Run `blade test <key>` blocks from YOUR worktree root only (the scratch dir
  is cwd-relative; never two test processes from one directory). The Bash tool cannot spawn g++.
- Record: `bench/RESULTS.md` sections 13-16 (ratio = expert / Blade, > 1 Blade faster), `bench/notes/gap-register.md`
  (G1-G53; G51-G53 are wave 3's), `bench/notes/round2-microkernel-plan.md`, `bench/notes/round2/brief-*.md` and
  `codex-review-*.md` (round-2 designs and attack reviews), `src/microkernels/README.md` (refuted schedules).
- Round-3 research: the brief for your item and its Codex attack review are quoted in your task; the full files are under
  `C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\{research,reviews}\`
  (read-only). The plan is `...\round3\PLAN.md`.

### Hard constraints (every one is checked by the Codex diff review)

1. Named outputs stay stored (bench protocol, `bench/README.md`); only compiler-inserted temporaries may disappear.
2. Strict / repro (`BLADE_FP_REPRO=1`, `where repro`): every fold chain keeps its exact seed, operand order and evaluation
   count; never drop or move a seed; never reassociate a strict chain; a licensed form needs the SOURCE licence and never
   inherits one from a map or another leg.
3. The C++ lane, the LLVM lane and the interpreter are twins: say which lanes you touch; `blade test interp` / `opt-diff` /
   `llvm` must stay green; byte identity under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`.
4. EVERY new scheduling / reordering admission consults the shared transitive write analysis
   (`Optimize.scheduleMayWriteIn programFuncs modul`, and `callbackTable` for parallel bodies) for ALL FIVE parties:
   producer kernel, consumer kernel, outer fold kernel, seed(s), intervening bindings -- an unresolved callee counts as a
   write; a writing party DECLINES (serial as-written) or refuses BL4005 when the source asked for omp on a racing body.
   Write the five-party matrix into the `blade plan` decision record and the gap-register row.
5. First-failure equivalence: a BL8003 / BL8006 / BL8013 panic fires at the same cell with the same partial output as the
   as-written program; stores of earlier named outputs happen before a later failure.
6. Knobs: name, default, registration (`Optimize.fs optimizerGates` for optimizer passes -- `blade test opt-diff` compares
   off vs on -- or `CliSelfTests.fs suiteClearedKnobs`), a CLAUDE.md env-table row, a `blade plan` record
   (`applied` / `declined -- <reason>`), and the census line `[omp] requested but emitted serial: ...` whenever a request
   is dropped.
7. Pins: corpus files carry `// EXPECT:` pins (type-spelled values), refusals `// ERROR: BLxxxx @ l:c`; `// WARN:` pins are
   strict both ways; emission needles go in the `tests/*.fs` block that owns the feature; new corpus files take the next
   free numbers (check `ls tests/corpus/<dir>`); never renumber existing files.
8. Line endings: `.fs` files are CRLF in the worktree (LF in the index), new corpus files LF; `git diff --check` clean.

### Deliverable

ONE commit per fix round (your first commit may be larger), message ending with
`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` (add your own model's line too if your session requires it).
Report: the commit id, the block footers (`<Block>: N passed, 0 failed` lines, verbatim), `git diff --check` result, what
you measured (if anything; one `blade run` A/B of your own program is fine, interleaved, medians), what you did NOT verify,
and every pre-existing defect you found (do not fix those outside your item; report them with a reproducer).

---

## W1-A2-rowview

_formerly `bench/notes/round3/briefs/W1-A2-rowview.md`_

### W1-2 / A2 -- the row-view inner fold in the strict threaded forms (gap G51), and the licensed helper arm

Read `wave1-builder-briefs.md#builder-preamble` first. Brief: `research/A-census.md` item A2 (sections "A2" and its implementation design;
emitted evidence in `scratchpad/round3/A-emit/stencil_lic.cpp` and `stencil_repro.cpp`). Review: `reviews/codex-review-A.md`
findings 4 (BLOCKER) and 5 (HIGH) -- close them exactly.

### The defect

stencil's input checksum is `reduce(method_for(range<I>) <@> lambda(i) -> reduce(u(i), (+)), lambda(a, b) where omp -> a + b, 0.0)`-shaped:
an outer `where omp` fold over rows whose inner fold runs over a ROW VIEW `u(i)` of a stored dense rank-2 array. Today:
- REPRO + threads: the G35 row-slot form (`BLADE_FOLD_ROW_SLOTS`) and Path B decline it -- `isCompleteFold`
  (CodeGenBinding.fs ~85-88) accepts only `IRReduceCompute`, and `IRTotality` (~173-195) has no row-view `IRReduce` arm --
  so the checksum runs serially (2.7-2.9 ms of stencil's mt8 repro time; wave-3 gap G51, formerly G48 on its branch).
- LICENSED: the emitted C++ puts `BLADE_OMP_SIMD_REDUCTION` on the ROW loop around an 8-lane scalar chain (the IIFE
  re-spells the row view per iteration); the vector helper `blade_fold::sum<double,8>` (`src/cpp/blade_fold_lanes.hpp`
  ~345-349; `model::sum` at ~141 is its scalar reference) is not used; the phase takes 1.5-1.9 ms where a plain read of the
  same 24 MB takes 0.7 ms.

### What to build

A. REPRO admission (the gap fix). Admit the row-view inner fold in the G35 row-slot form AND Path B when ALL of:
   base array stored, dominating, non-virtual, dense rank 2, rows contiguous, correct index identity (the view's axis is
   the base's second axis); the inner fold is complete and total (extend `isCompleteFold` and `IRTotality` with the exact
   row-view `IRReduce` case; return the real row work count for `rowslots_min`); BOTH seeds (inner and outer) preserved
   exactly, evaluated once in their original scope and captured; the unseeded inner fold keeps its head-peel semantics and
   its empty-row BL8003 at the original site; no cross-binding motion (say so in the record). Five-party matrix
   `rowViewFoldAdmission` per review A/5: producer (the stored base; no producer evaluation moves), consumer (the whole row
   callable incl. leading `IRLet`s and the inner fold callable through `scheduleMayWriteIn` + `IRTotality.auditCallable`),
   outer kernel (same audit; its own explicit threading request; ascending outer combination kept), seeds (both), intervening
   (every statement whose relative order changes; reject writes through aliases to the base or captured state).
   Result bits: identical to the serial as-written chain under REPRO + CONTRACT=off (each row's chain from its own seed in
   ascending j, the outer chain from its seed in ascending i).
B. LICENSED helper arm (separate, gated, measured -- NOT identity-preserving, so never on a strict chain): when the inner
   fold is reorder-licensed (builtin `+` / `where omp`, no `where repro`, float type, `BLADE_FOLD_LANES` on), lower the
   inner row fold to `blade_fold::sum<T,8>(row, n)` COMBINED WITH THE ORIGINAL SEED exactly once (review A/4: `sum` returns
   the sum of the row; `reduce(u(i), op, s)` is `s op row...`; the literal rewrite that dropped `s` was the BLOCKER), and
   drop the outer `omp simd` around it (an explicit licensed-only choice, recorded). Knob: it rides `BLADE_FOLD_LANES`
   (already in suiteClearedKnobs) -- the plan record must name it; `blade test fold-lanes` gets the needle.

### Tests

- corpus `tests/corpus/omp-reduce/` or `loops/` (next free numbers): the stencil checksum shape with (i) a nonzero inner
  seed, (ii) an unseeded inner fold, (iii) an empty row (BL8003 at the original site), (iv) a singleton row, (v) signed
  zeros, (vi) a non-commutative inner kernel (`3*a + b`) -- all pinned bitwise against the nested-range spelling under
  REPRO; (vii) a writing inner kernel / a view alias writer -> declined with the census line.
- `tests/OmpTests.fs`: row-slot case asserting the row-view fold is TAKEN (`fold form: threaded complete rows` with the
  row-view note) at T=1 and T=8 with identical bits; Path B case likewise; the licensed arm: needle `blade_fold::sum<` and
  NO `BLADE_OMP_SIMD_REDUCTION` on the row loop, values within the licensed tolerance vs the old form, seeds honoured.
- `blade plan bench/kernels/stencil/stencil.blade` under `BLADE_FP_REPRO=1`: the checksum's record says applied.

### Blocks to run and report

omp-reduce, omp-pragma, omp-coverage, fold-lanes, cell-epilogue, optimize, loops, index-types, cli, corpus-wiring,
interp loops / omp-reduce (via `blade test interp <dir>`), opt-diff loops. One `blade run` A/B of stencil at full size,
REPRO, T=8, old vs new binary interleaved (5 rounds each, medians) -- directional only; the harness A/B is the orchestrator's.

Update the G51 row (FIXED, with the matrix and the pins) and `bench/RESULTS.md` only if you add a line to section 16's
gap list (do not renumber anything).

---

## W1-T4-maskagg

_formerly `bench/notes/round3/briefs/W1-T4-maskagg.md`_

### W1-4 / B-T4 -- maskagg selection structures: positions demand census (T4b) and the mask-site gather (T4a)

Read `wave1-builder-briefs.md#builder-preamble` first. Brief: `research/B-relational.md` item T4 (and its probes in `B-work/`). Review:
`reviews/codex-review-B.md` findings 8, 9, 10, 11 (the T4 rows) -- close them. Round-2 base: the B4 compaction fusion
(`Optimize.planCompactionFusion` ~2722-2925; `bench/notes/round2/brief-B-epilogues.md` B4; pins `tests/corpus/index-types/515-521`;
`blade test index-types`), which already counts in the mask pass, fuses the position fill with the gather, removes the
square pool, feeds the strict chain per 256-cell block, keeps `keep` stored and ALWAYS builds positions.

### What to build

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

### Tests and blocks

index-types (515-521 unchanged in value; new 522+ for T4a/T4b positives and each demand kind), sql, loops, optimize
(decision cases for the census and the gather), cli, corpus-wiring, interp index-types / sql, opt-diff index-types, llvm
index-types. `blade plan bench/kernels/maskagg/maskagg.blade` in both regimes: positions elided (maskagg's `y` has no keyed
read -- verify) and the gather at the mask site. One `blade run` A/B of maskagg at full size, serial licensed and repro,
old vs new interleaved (directional). Report logical traffic before / after (the brief claims 30 -> 17 MB licensed,
26 -> 13 MB repro) from the emitted loops, not from timing.

---

## W1-defects

_formerly `bench/notes/round3/briefs/W1-defects.md`_

### W1-3 -- deferred-materialization defects in the C++ lane (four reproducers, BL9002 today)

Read `wave1-builder-briefs.md#builder-preamble` first. These are correctness fixes, no performance claim. Each reproducer stays its OWN corpus
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

---

## W1-runner

_formerly `bench/notes/round3/briefs/W1-runner.md`_

### W1-1 + W1-6 -- runner tasks (measurement only, no code)

You are the round-3 test runner. Box rules: one timing process at a time; before any timing, audit `Get-Process` for stray
work (a backgrounded `find /` once burned a core beside an A/B); never time while a build or test runs; ucrt64 first on
PATH; `OPENBLAS_DIR=C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64`. Harness: `bench/harness/run.py` needs one MIRROR ROOT per
binary / config (`C:\bt_r2ab\<name>\{bench, bin\Release\net10.0}`, no .git, no blade.toolchain.json); knob arms are a
mirror whose `bench/harness/config.json` `blade_knobs.all` carries the knob (the harness strips inherited BLADE_* env);
smoke evidence is keyed per kernel|regime|arm on (source, Blade.dll, config) hashes -> smoke only the pairs you bench.
Scripts to adapt: `bench/notes/round2/w2-scripts/{mirrors.ps1, smoke.ps1, driver.ps1, analyze.py}` (PowerShell `-File`
passes a list as ONE string -- call with `-Command "& 'script' -Mirrors a,b,c"`; `$K` and `$k` are the same variable).

### W1-1 -- A1 experiment: the ordered scatter instead of the G35 row slots under mt8 repro (zero code)

Binary: the integration tip (the orchestrator names it). Mirrors: `r3a-def` (default config) and `r3a-noslots`
(`blade_knobs.all = { "BLADE_FOLD_ROW_SLOTS": "0" }`; BLADE_ORBIT_SCATTER stays on). Cells: pairkernel and comoment3,
`mt8-repro-native` (and `serial-repro-native` as the control that both mirrors must agree on). Before timing, verify from
the smoke bundles' `plan_fold_form` evidence that the default takes `threaded complete rows` and the knob-off mirror takes
`ordered orbit scatter` (G35 declined by its knob), and that the producer form is unchanged; compare the two arms' printed
checksums bit for bit (they must be identical: both are strict forms). Interleave def / noslots / def / noslots, 21 rounds,
and report per cell: medians, NEW/OLD ratio with a bootstrap CI (analyze.py), expert ratios, block class, and -- from the
run records if the harness exposes phases -- the consumer phase separately. Verdict per kernel: scatter wins / row slots
win / within noise (review A/2: judge each kernel independently; do not adopt a threshold from two shapes).

### W1-6 -- anti_g quiet re-measure

Mirrors: `r3a-def` (anti_g default 1) and `r3a-antig0` (`blade_knobs.all = { "BLADE_TILE_TUNE": "anti_g=0" }`). Cells:
antisym `serial-licensed-native` and `mt8-licensed-native`, THREE interleaved sessions per mirror per regime (the signal
is 1-5%). Report medians, ratios with CIs, and whether any block is live; the default flips only if auto wins beyond the CI
on LIVE blocks in both regimes (round 2: auto 0.954 [0.904, 0.996] mt8 indicative; wave 3: auto 1.3-1.4% slower serially,
live).

Write `scratchpad/round3/gates/w1-runner-report.md` with the tables, the bundle paths, the Blade.dll hash, the box state,
and the two verdicts.

---

## W1-workloads

_formerly `bench/notes/round3/briefs/W1-workloads.md`_

### W1-5 / C -- two new bench workloads: N07 (resident two-FIR / sibling-map fusion) and N09 (tensorop)

Read `wave1-builder-briefs.md#builder-preamble` first. Brief: `research/C-workloads.md` sections N07 and N09 (complete sources in
`scratchpad/c-work/n07.blade`, `n09.blade`; all ten `blade check` today). Review: `reviews/codex-review-C.md` -- per-workload
verdicts N07 ADD AFTER FIXES, N09 ADD AFTER FIXES; findings 1 (BLOCKER, N09 outputs), 8 (storage claims), 10 (regimes /
sizes / licences), 11 (inputonly policy), 13 (N07 scope). No compiler change in this item: it adds two kernels to the suite.

Templates: `bench/kernels/<k>/{kernel.json,spec.md,<k>.blade,natural.f90,expert.f90}` for stencil (a halo kernel), dot /
fusedchain (inputonly twins), gram (matmul-class expert); protocol `bench/README.md` and `bench/kernels/README.md` (no
power-of-two extent anywhere; named outputs stored in every arm; inputonly twin when input generation is > 5% of the
time; checksum reads every stored output; pins at smoke size in `bench/harness/validation/pins*.json` -- see how the
harness generates / checks them, `bench/harness/run.py check <kernel>`). Fortran: gfortran 16.2 ucrt64 and ifx via
`bench/fortran/build_ifx.cmd`; the harness builds and runs the twins (`run.py check` at smoke size is your gate; it
compiles Fortran -- fine on this box now).

### N07 -- `twofir`: the honest name is "resident two-output FIR / sibling-map fusion"

Not ghost reuse (review C/13: in-memory `Chunked` adds nothing; the per-segment ghost path exists only for streamed
sources). Two direct FIR maps over one stored input with DIFFERENT output domains (y1's six-only fringe centres outside
y2's domain); both y1 and y2 named outputs, stored; separate +0 `prodsum` checksum chains, combined s1 + s2; an inputonly
twin (input generation is a large share). Natural twin: two separate loops (one per output). Expert twin: one fused direct
loop producing both outputs from the same loads (a 17-cell ring is an OPTIONAL reviewed alternative, not the expert).
Sizes: non-power-of-two (the brief's n ~ 3,000,017 or similar); smoke size small; both ~48 MB principal payload -- check
the 64 MB rule with the live input included (review C/8). Hypothesis: parity (Blade already fuses sibling maps that read
the same input? -- verify with `blade plan`; if the two maps are emitted as two passes, that is the round-3 transformation
this workload measures: say so in spec.md).

### N09 -- `tensorop`: named-axis tensor-product operator action

Outputs: X, A, B, C and Y ONLY (review C/1: the intermediates T1 / T2 are the compiler's; never required stores). The
contraction order I then J then K is the specification; the expert may choose intermediate lifetimes / layouts but not the
factor order. Baseline Blade spelling = the existing `m.mode_product` / unfold-`matmul` idiom (`MathElaborate.fs` ~349-367;
`m.matmul` routes to the native 6x8 `dgemm_nn` tile) -- measure THAT spelling first; a strided-contraction recognizer is a
later item, not this one. Strict pins from an ordered scalar oracle (not numpy einsum with free reassociation); the two
unseeded reduce chains start from their first product (keep that seed distinction in the oracle). Natural twin: three
textbook triple loops in the specified order; expert twin: blocked / dgemm-shaped contractions with the same factor order.
Sizes non-power-of-two, ~28 MB for the four tensors plus factors; smoke small. Regimes: serial + mt8, licensed + repro
(fastmath if the kernel has a fold the vendor flags change). Hypothesis: today 3-10x LOSS (brief) -- record the mechanism.

### Both

Add every legal `where omp` request on independent maps, generation and checksums (review C/10) with no captured writes;
document each loop's form from `blade plan`. Record the storage estimate per arm (payload + scratch) in spec.md.
Validate: `python bench/harness/run.py check twofir` and `check tensorop` at smoke size -> `0 failure(s)` for all arms
(Blade both lanes, gfortran natural / expert, ifx natural / expert, inputonly twins); pins generated per the harness's
procedure (read `bench/harness/README.md`); `blade test` has no block for new kernels -- run `blade check` on both sources
and `blade run` once at smoke size in both regimes. No full-size timing (the orchestrator's A/B does that). Report the
checksums, sizes, the forms `blade plan` reports, and anything the harness flagged.
