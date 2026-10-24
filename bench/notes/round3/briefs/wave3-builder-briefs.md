# Round-3 wave-3 builder briefs (condensed)

The wave-3 preamble addendum (what wave 2 taught, as rules) and the four item briefs as handed to the builders (the shared preamble is section 1 of `wave1-builder-briefs.md`).

Sections (each is one former scratchpad file, verbatim): [`wave3-builder-briefs.md#w3-preamble-addendum`](#w3-preamble-addendum), [`wave3-builder-briefs.md#w3-1-t3-groupagg`](#w3-1-t3-groupagg), [`wave3-builder-briefs.md#w3-2-t2-hashjoin`](#w3-2-t2-hashjoin), [`wave3-builder-briefs.md#w3-3-p1-commit-prototype`](#w3-3-p1-commit-prototype), [`wave3-builder-briefs.md#w3-4-p2-pairaction`](#w3-4-p2-pairaction)

---

## W3-PREAMBLE-ADDENDUM

_formerly the scratchpad's `round3/briefs/wave3-builder-briefs.md#w3-preamble-addendum` (appended at the wave-3 records commit)_

## Wave-3 addendum to the builder preamble (read BUILDER-PREAMBLE.md -- now section 1 of
`bench/notes/round3/briefs/wave1-builder-briefs.md` on the integration branch -- then this)

Where things are now: the round-3 record is CONDENSED. Codex diff reviews live as sections of
`bench/notes/round3/reviews/wave1-codex-reviews.md` / `wave2-codex-reviews.md` (cite `reviews/wave2-codex-reviews.md#codex-diff-w2-3`);
briefs likewise in `briefs/wave*-builder-briefs.md`; the A/B records are `w1-ab.md` / `w2-ab.md`; RESULTS section 17 is the summary.
The gap register reaches G70; your item's row number is in your brief. Your worktree is cut from the integration tip named
in your task (it includes every wave-2 item, W2-4 rounds 4-5 among them).

Rules wave 2 added (each was a review finding at least once):

1. FAIL CLOSED. Your admission is a closed whitelist of shapes you have proved; everything else declines with a record that
   names the first failed condition. Do not enumerate "the forms we know allocate" -- enumerate the forms you ADMIT.
2. A lifetime, placement or ordering fact that a later pass could invalidate is an IR-LEVEL NODE (every pass preserves it,
   every renderer honours it, both lanes implement it, the interpreter accepts it as a no-op), never a side table keyed by a let.
3. ONE write analysis: `Optimize.scheduleMayWriteIn programFuncs modul` with `intervalPayloadOf` for intervening bindings and
   `callbackTable` for parallel bodies; storage is proved by `storedArrayProof`. Reuse them; a second predicate is a finding.
4. Say what is unproved. "Tested on these witnesses, not proved in general" is the phrasing; a record that says "honoured by
   every pass" invites the counterexample.
5. Testing is LIGHT by the user's instruction: run your item's blocks and the directly shared ones; never the full suite,
   `test interp`, `opt-diff` across the corpus, or a harness A/B. The orchestrator runs the gates and A/Bs on a quiet box.
6. New kernels: non-power-of-two extents, named outputs stored in every arm, an input-only twin, both expert twins honest
   about what they fuse; the Codex review of the kernel judges the twins' fairness before any timing.
7. The box is shared with other builders: no multi-core timing; one-core directional runs only where your brief allows one.
8. ONE squashed commit on your branch on top of the tip, both co-author lines (`Claude Opus 5.5`, `Claude Fable 5.1`), never
   push, never rebase, never touch another worktree. Report: sha, files per lane, the five-party matrix as recorded,
   footers of every block you ran, what you could not verify, what you noticed out of scope (do not fix it).

---

## W3-1-T3-groupagg

_formerly the scratchpad's `round3/briefs/wave3-builder-briefs.md#w3-1-t3-groupagg` (appended at the wave-3 records commit)_

## W3-1 -- T3 aggregate-only grouping, REDESIGNED: one pass, exact state machines, three regimes, strict first

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and `wave3-builder-briefs.md#w3-preamble-addendum`
first. Gap row: **G71**. Research: `bench/notes/round3/research/B-relational.md` section 2 "T3" (the shape, the one-pass
form, the legality argument, the refusals, the admission table), section 3 (`groupagg` row), section 4 (`GroupAggTable`,
planner placement), section 5 (DROP: < 1.3x OLD at G = 1009; drop the licensed-float arm if one-pass loses > 10% at G <= 3).
Attack review `bench/notes/round3/reviews/codex-review-B.md` findings 1-4 are THE CONTRACT of this redesign:

1. **First-failure equivalence (B/1).** Accumulation MOVES from the peels up to `group_keys`. v1 therefore accepts ONLY value
   sources that are already STORED (`storedArrayProof`), DOMINATING (bound before gk) and shape-compatible, and seeds that
   are total, dominating and row-independent (a literal, a dominating scalar binding, a pure total expression of those).
   A deferred value producer, a seed bound between gk and its peel, a seed that can fail (a keyed read, an empty unseeded
   reduction), a value array bound after gk: DECLINE (the CSR + gather stay). Keep BL8006 key validation at gk's span and
   order; keep each BL8003 at its original peel for the FIRST empty group in group order; an intervening BL8013 between gk
   and a peel still fires before that peel's BL8003 (the pass is total, so hoisting accumulation cannot move an abort --
   state that in the record and pin it). Named grouped outputs are stored before any later failure; private accumulators are
   not permission to publish later outputs early.
2. **Exact state machines (B/2).** FOUR, never one: UNSEEDED (the first value of each group is copied as the state and
   consumed exactly once -- the first-row update is skipped), SEEDED (the exact original chain from the seed, including
   statically declared EMPTY groups, which get the seed), PRODSUM (its own initialization and rounding preserved), COUNT.
   No universal `-0.0` identity substitution for a generic callable. Pins: singleton group, empty group seeded and unseeded
   (BL8003), nonzero seed, signed zero, the non-commutative `lambda(a, b) -> 3 * a + b` (exact integers: pins the ORDER).
3. **Three regimes kept apart (B/4).** Positional `Idx<N>`: groups 0..G-1 by key value INCLUDING empty groups; EnumIdx:
   the DECLARED enumeration order, negative members VALID (never dropped; pin `[7, -2, 3]` declared unsorted); dynamic: FIRST
   occurrence (pin keys `[7, 3, 7, 9, 3]` -> counts `[2, 2, 1]`); hash-table iteration order never defines output order;
   `extents(gk)` stays the stored array of group sizes. The negative-key DROP applies where today's group_keys applies it
   (the dynamic regime), exactly as today.
4. **Strict first (B/3).** v1 is the one-pass form for every regime under the STRICT association (bitwise the two-pass
   form under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, both lanes: each group's chain keeps its operands, order, seed and
   left association; interleaving across groups reorders only independent chains). The LICENSED float form (the ascending
   chain in place of today's per-group K-lane tree) is a legal association CHANGE with no 1e-9 guarantee (`[1e16, -1e16, 1,
   0, 0, 0, 0, 0, 1]` gives 2 vs 1): it lands ONLY as a separately gated arm (`BLADE_GROUP_AGG=licensed`) with its own
   cancellation fixtures reporting the changed bits, and its own directional number; the default keeps the strict pass under
   repro and today's licensed form otherwise unless you can show the licensed one-pass form bitwise equals today's (it
   does not for floats). Integer folds are exact either way and may take the one pass in every build.

### The pass

Per row i in source order: read `__k = k[i]` (negative-key drop and BL8006 positional guard exactly as today, at gk's span);
find g (positional g = k; EnumIdx static lookup; dynamic ONE find-or-insert in the C core, numbering by first occurrence --
this retires the O(n*G) discovery for every dynamic grouping in the LLVM lane too, `EmitLlvm.fs` ~3790); per state
machine update `acc_j[g]`, `cnt[g]++`. After the pass `offsets` is derived from `cnt` for `extents(gk)`; there is no perm and
no gathered pool (the rows stay null, as in the positions elision); each peel binding becomes `out[g] = acc_j[g]`.

### Admission -- `groupAggAdmits` (every party through `scheduleMayWriteIn` + `intervalPayloadOf`, parallel bodies through `callbackTable`)

producers k and every v_i stored (`storedArrayProof`), never assigned, dominating; each peel kernel W+R and in the whitelist
(`reduce(r, k[, init])` with k pure total two-parameter, `prodsum(r, r)`, `prodsum(ra, rb)` over same-gk rows, `extents(r)`);
each outer fold kernel W+F; each seed W+F, total, dominating, row-independent; every binding gk..last peel W+F; refusals: a
row-reading consumer (`r(0)`), co-iteration with a non-gk operand, a printed or escaping grouped array (printSelection and
the demand census), multi-key / string keys, a kernel outside the subset or one that may fail / write / call unaudited, a
`where omp` peel (`sql-group-by/033`), `.stream` sources, a group fold writing a captured counter. The five-party matrix in
the plan record (`[group-aggregate v1] gk: applied [...]` / `declined -- <first failed condition>`) and G71.

### Lanes / knob / records

Both lanes (C++ grouping + peel emitters; LLVM positional and EnumIdx in v1 and the dynamic regime through the C-core
hash); interpreter unchanged (values identical: `test interp` on the new files). `BLADE_GROUP_AGG` unset/1 = the strict
one-pass form (+ integer licensed), `licensed` = the float licensed arm, 0 = off, in `optimizerGates`; CLAUDE.md row;
docs/features/sql.md section 7/8 aggregate-only paragraph; G71 with the matrix, the regimes, what is declined, what is
unproved.

### Tests

corpus `sql-group-by/064+` (next free: check `ls`): one pass over dynamic sum / prodsum / extents; positional empty groups
seeded and unseeded (ABORT BL8003 at the peel); EnumIdx declared-unsorted with a negative member; dynamic first-occurrence;
the `3*a+b` fingerprint; compound key source; the four state machines each pinned; declines for a row-reading second peel,
a printed grouped array, a `where omp` peel, a deferred value source, a late seed; first failure: BL8006 at gk then an
intervening BL8013 before a later BL8003; `/051` and `/056` under the gate unchanged. OptimizeTests decision cases per
matrix row and emission needles both lanes (one pass, no perm, no gathered pool; gate-off restores the CSR + gather);
`sql`, `optimize`, `cli`, `llvm sql-group-by`, `interp sql-group-by`, `opt-diff sql-group-by`, `docs`, `corpus-wiring`.

### The kernel `groupagg` (bench/kernels/groupagg)

In a function: `gk = group_keys(key)`; peels `reduce(r, (+))` and `prodsum(r, r)`; `c = extents(gk)`; outputs = the two
grouped arrays and c, named and stored; G = 1009 positional, n = 1,000,003 (non-power-of-two), `_dyn` variant with dynamic
keys (first-occurrence numbering); input-only twin; natural twin `s(g) = sum(x, mask = key == g)` (O(nG), shrink or tier
it), expert twin a one-pass direct-index accumulator (dynamic: open addressing with first-seen numbering); kernel.json
parallel notes honest (serial kernel; is there a legal mt8 arm? only if the per-group chains are independent and the regime
is strict -- say so); spec.md names the outputs and the exact chain order; strict pins from an ordered scalar oracle.
Directional, one core: groupagg OLD vs NEW, 11 interleaved, strict; the DROP line: < 1.3x OLD at G = 1009 -> record, stop.

### Scope

Not: `group_bucket` (v2), means / tuple joins (v1.1), T2 / T4 changes, the harness A/B. Report as the addendum says.

---

## W3-2-T2-hashjoin

_formerly the scratchpad's `round3/briefs/wave3-builder-briefs.md#w3-2-t2-hashjoin` (appended at the wave-3 records commit)_

## W3-2 -- T2 hash equijoin at PILOT size: a stable forward-filled CSR, left-major / right-minor probe, the named relation stored

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and `wave3-builder-briefs.md#w3-preamble-addendum`
first. Gap row: **G72**. Research: `bench/notes/round3/research/B-relational.md` section 2 "T2" (the source shape, today's six
passes at 19.4 B per pair, the recognizer, the physical form, legality, refusals, admission), section 3 (`hashjoin` row:
infeasible today at full size), section 5 (DROP: if the coordinator keeps the NAMED R4 spelling stored AND rejects the inline
spelling, drop v1 -- a join operator is a language question; otherwise at pilot size NEW < 0.8x the expert CSR join ->
return to design). Attack review `codex-review-B.md` findings 5-6 and 10-11 are the contract:

1. **Order (B/5).** The compound's lex storage order is left-major / right-minor. The CSR must be FORWARD-FILLED (stable),
   the probe i ascending, and EVERY right row j in the bucket retained ascending: bags survive. Pin the 2x2 duplicate case
   (left keys `[1, 1]` amounts `[1, 10]`; right keys `[1, 1]` amounts `[2, 3]` -> values `[2, 3, 20, 30]`, fingerprint
   `reduce(matched, lambda(a, b) -> 3 * a + b, 0)` = 171; a dedup gives too few rows; prepend-built buckets 209; a right-major
   probe 273), interleaved different keys, all-equal skew, negative keys (they MATCH: equality has no negative-key drop), no
   matches (seeded = the seed; unseeded `// ABORT: BL8003`), tails. An out-of-domain positional left key is "no match",
   never BL8006.
2. **Honest cost accounting (B/6).** Publish SEPARATELY: operations, logical traffic, allocated / live bytes, measured peak
   working set. Today's form at the pilot (N 2003, M 1009: P = 2,021,027 pairs) is ~41-57 MB of traffic and ~20.7 MB of live
   pools; the CSR form is O(N + M + J). J is unknown beforehand: a COUNTING pass sizes the output (or growth with a recorded
   policy); a general all-equal join has J = N*M and cannot satisfy the 64 MB rule while storing J -- bound the generated
   key distribution (K = 401 keys, J ~ 2.0e6... no: at the pilot J ~ P / K ~ 5,040 matches; state the distribution and J)
   or decline oversized results with a record. Full size (N 100,003, M 20,011) is NOT benched this wave.
3. **Outputs (B/10).** Only the outputs the spec names are stored: `matched` (the named compact relation) stays STORED and
   its consumer folds unchanged; the inline `products_map` / `equal_keys_map` / their masks are compiler temporaries that MAY
   disappear; the NAMED R4 spelling (`equal_keys`, `keep`, `products` as let-bound arrays) keeps every named array stored and
   is therefore not accelerated by this item -- the kernel uses the inline spelling and the spec says why.
4. **Admission (B/11)** -- `equiJoinAdmits`: producer kernels (pk, `==`, the identity predicate) W+F and in the `totalScalar`
   whitelist; the four sources stored (`storedArrayProof`), never assigned; J's whole value; no consumer moves (the folds read
   the stored J), so the outer kernel and seed are untouched but still named in the matrix; refusals: non-whitelisted pk /
   `==` (integer `/ % ^`, calls, captures), float keys, more than two axes, a non-identity predicate, a conjunctive key kernel
   (v1.1), a census miss (any use of J but `extents(J)` / `reduce(J, k[, init])` with k pure total -- a printed, keyed,
   `present`-queried, partially indexed or passed-on J keeps the hashed index and the NM-bit mask).

### Physical (C++ v1)

Recognized at `CompoundInits[J] = (dense, mask)`: dense = `IRCompute(IRApplyCombinator {Arrays = [a1; b1]; not IsCoIteration;
pk})`, mask = `IRMask(E, lambda(e) -> e)` with E the outer product over `[a0; b0]` with kernel `l == r` (either order); a0 / a1
share the left nominal axis, b0 / b1 the right (index identity, not extent); integer keys. Build a CSR over b0 (a positional
key type gets a counting sort; otherwise the T1 C core `blade_member.h` as the key-to-bucket map); probe i ascending, walk
each bucket in ascending j, write `pk(a1[i], b1[j])` into `compact[q++]` after the counting pass sized it; J stored through a
cardinality-only `compound_index_t<2>` constructor; consumers unchanged. The LLVM lane DECLINES in v1 with a truthful
record (`declined -- llvm lane: the hash equijoin is not built in this lane (v1.1)`); the interpreter is unchanged (it is
the independent oracle: `test interp` on the new files). The D-B1 fix (wave 1) is in the tip.

### Knob / records / tests

`BLADE_HASH_JOIN` unset/1 on, 0 off, in `optimizerGates`; plan record `[hash-join v1] J: applied [stable CSR over <right>,
probe left-major / right-minor, counting pass sized J = ...; <matrix>]` / `declined -- <why>`; CLAUDE.md row; docs/features/sql.md
(section 7/8 join paragraph; close open item 1 if it is this); G72 with the matrix, the cost table, what is declined and
unproved. Tests: a NEW discovered directory `tests/corpus/sql-joins/` (in `blade test sql`; add it to Corpus Wiring's facts):
001 the R4 fixture (`match_count = 4`, `total = 120`); 002 the order fingerprint; 003 negative keys match; 004 / 005 no
matches seeded / unseeded (ABORT BL8003); 006 a keyed `J(0, 1)` declines, same values; 007 a writing payload declines;
008 / 009 dynamic and positional keys; 010 the 2x2 duplicate case; 011 an out-of-domain positional left key. OptimizeTests
decision cases per matrix row, emission needles (the CSR build, the counting pass, no all-pairs `==` pool; gate-off restores
the six passes); `sql`, `optimize`, `cli`, `interp sql-joins`, `opt-diff sql-joins`, `llvm sql-joins` (all refusals, recorded),
`docs`, `corpus-wiring`.

### The kernel `hashjoin` (bench/kernels/hashjoin), PILOT size only

The inline T2 spelling; outputs `match_count`, `total`, and `fp = reduce(matched, lambda(a, b) -> 3 * a + b, 0)` (exact
Int64: pins the ORDER); N 2003, M 1009, K 401 (bounded key distribution, J stated); input-only twin; natural twin = nested
`do` loops (O(NM), fine at the pilot); expert twin = a counting-sort CSR on the right keys over [0, K) then a left probe
(serial; mt8 only if a legal output-owned form exists -- say so in kernel.json); spec.md names the outputs and the order.
Directional, one core: OLD (six passes) vs NEW, 11 interleaved; report operations / traffic / live bytes / peak WS too.

### Scope

Not: full size, the LLVM arm, conjunctive keys, a fold-into-probe (v1.1 needs B4a's snapshot proof), the harness A/B.

---

## W3-3-P1-commit-prototype

_formerly the scratchpad's `round3/briefs/wave3-builder-briefs.md#w3-3-p1-commit-prototype` (appended at the wave-3 records commit)_

## W3-3 -- D/P1 producer-fused ordered commit: a COSTED PROTOTYPE, not a planner item

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and `wave3-builder-briefs.md#w3-preamble-addendum`
first. Gap row: a NOTE on G35 / G44, no new row unless the prototype wins. Research: `bench/notes/round3/research/
D-orbit-regions.md` section 3 (P1: the recognized shape, the schedule and why it is the serial scatter exactly, why not waves /
a 2-D DAG / a standalone tile, refusals, lanes, counts, implementation sketch, DROP line). Attack review
`codex-review-D.md` findings 1-2 (P1 VERDICT: BUILD AFTER FIXES -- order is defensible; build a costed prototype BEFORE the
planner) and 3-4 (validation and the five-party contract) are the contract. Wave 2 moved the baseline: pairkernel mt8 repro
is now the ordered scatter at 7.64 ms (expert 6.42 / 6.21), the row slots 9.0 ms; parity needs <= 6.4 ms.

### What to build

A standalone C++ prototype (bench/notes/round3/prototypes/p1-commit/, not the compiler): take `blade emit` of
`bench/kernels/pairkernel/pairkernel.blade` under `BLADE_FP_REPRO=1` with threads (the wave-2 tip's emission: the threaded
`comm` producer `kmat`, then the serial ordered scatter for `ss`), factor the serial scatter's per-group body (`tri`'s group
body in `blade_orbit_scatter.hpp`, rows i0..i0+G-1) into a `tri_group` call (no behaviour change for `tri`), and write the
commit schedule beside it:

```text
prelude: R[0..n) = s_in; out = s_out; ticket = 0; claim = 0
#pragma omp parallel num_threads(T)
  for (g = claim.fetch_add(1); g < ng; g = claim.fetch_add(1)):      // explicit ascending canonical row-group claims
      produce pool rows gG .. gG+G-1 (the producer's own cell kernel)   // ALL rows the interval reads, before commit
      wait until ticket.load(acquire) == g                             // _mm_pause, yield after N spins
      tri_group<G,B>(pool, n, R, gG, f, k_in)                           // == the serial scatter's group g, THIRD=false
      for i in group: out = k_out(out, R[i])                            // ONE outer accumulator, increasing i, no subtotals
      ticket.store(g + 1, release)
```

Review D/1's corrections are part of the design: the ticket unit is an EXPLICIT canonical row-group interval (G-aligned
claims), not a thread id or a readiness ordinal; production of every row the interval reads finishes before the commit; the
outer accumulator advances through completed rows in increasing i with no per-group subtotal and no reseed (the
`[1e16, -1e16, 1]` regrouping counterexample); the tail group (n = 2503, G = 4: 625 full groups + a 3-row tail) is committed
as its own interval; acquire / release on the ticket publishes R and out; relaxed unique claims; the producer kernel total
(matern52's sqrt / exp / divide pass IRTotality); the callable repro boundaries and producer stores preserved.

### What to measure (quiet box: coordinate with the orchestrator -- the box is shared; run ONLY when told it is free)

Interleaved, 21 rounds, `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, OMP_DYNAMIC=false: (a) the producer alone (control),
(b) producer + today's default (the ordered scatter, serial), (c) producer + G35 row slots (`BLADE_ORBIT_PULL=0
BLADE_FOLD_ROW_SLOTS=prefer` emission), (d) producer + the ticket commit, at G in {1, 4, 8} and T in {1, 3, 8} plus a
restricted actual team (`OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=2`). Instrument (d): per-group production time and commit
service time, total wait in CORE-time with the stated denominator (summed worker waits over T x wall), the longest
predecessor stall, the actual team, the handoff count. Raw bits of R and out equal to the serial scatter's in EVERY
configuration (a mismatch is a bug, not a result). Review D/2's lower bound: with S = serialized commit service (0.78-1.25 ms
at the brief's per-cell cost) and 43.2 core-ms of production, end-to-end >= max(S, (43.2 + S) / 8) = 5.5-5.6 ms before
overheads -- report where the prototype lands against it.

### Decision rule (recorded either way)

The planner integration (`compactRowCommitAdmissible`, `Form = RowCommit`, the C++ outer-header arm, the LLVM serial-scatter
fallback, `BLADE_ORBIT_COMMIT` in `optimizerGates`, raw-bit tests across commit on / off, G35, scatter, LLVM and interpreter
under REPRO + CONTRACT=off with the NaN-payload contract stated) is a LATER wave's item ONLY if (d) beats (b) by >= 1.5 ms
end-to-end with paired CIs AND the ticket waits are below 20% in the stated denominator. Otherwise DROP with the numbers in
RESULTS 17.6 and the G35 / G44 rows ("the producer-fused commit measured at ... ms vs the scatter's ...: not taken").

### Report

The prototype's location and build line; the schedule as implemented (the G-aligned claim, the tail, the ticket); the
raw-bit identity result; the measurement table (configurations x G x T, medians and paired CIs, wait / stall /
handoff instruments); the lower-bound comparison; the decision; what you could not measure. No compiler change in this item.

---

## W3-4-P2-pairaction

_formerly the scratchpad's `round3/briefs/wave3-builder-briefs.md#w3-4-p2-pairaction` (appended at the wave-3 records commit)_

## W3-4 -- D/P2 rank-2 orbit ACTIONS over a stored pool, single channel first; the `pairaction` kernel

Read `BUILDER-PREAMBLE.md` (section 1 of `bench/notes/round3/briefs/wave1-builder-briefs.md`) and `wave3-builder-briefs.md#w3-preamble-addendum`
first. Gap row: **G73**. Research: `bench/notes/round3/research/D-orbit-regions.md` section 4 (P2: the shape, legality,
refusals, admission, the `pairaction` / `momenttower` rows, the implementation sketch, DROP: the multi-channel form only if the
joint pass is >= 2x three single-channel scatters). Attack review `codex-review-D.md` findings 4-6 and 8 (P2 VERDICT: BUILD
AFTER FIXES -- start with stored rank-2 actions; split out the deduction and the tower) are the contract. Round 2's ordered
scatter (`OrbitFold.planStrict`, `blade_orbit_scatter.hpp`, `EmitLlvm.emitOrbitScatter`) and wave 2's pull (G66) are the
machinery you extend; `symmetry/132-139` are its pins.

### The shape (v1: ONE channel, a STORED SymIdx<2, n> pool S)

`u = method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> g(S(i, j), V1(i), V1(j), ...), k, s)`:
an `IRApplyCombinator` over `range<I>` whose kernel body is one `IRReduceCompute` level over `range<I>` (S's own tag); g a pure
total scalar function of x = S(i, j) (or S(j, i)), of rank-1 dense arrays read at the chain's OWN coordinate i or at the
PARTNER j, and of invariant scalars; no other use of i, j. (The existing scalar-total walk is the special case "outer level a
fold, g coordinate-free".)

### Legality (review D/5: separate ROLE terms; never one weighted term at both endpoints)

One pass over the canonical cells: (a, b) with a < b gives row chain a the term g(x, own = V(a), partner = V(b)) and row chain b
the term g(x, own = V(b), partner = V(a)) -- TWO evaluations with swapped own / partner weights (S(i, j) * V(i) is 6 at (0, 1) and
10 at (1, 0) for S01 = 2, V = [3, 5]: the packed READ is invariant, the cell TERM is not); a = b gives one term. Each term is
bitwise the source's term at that position (symmetric storage answers (a, b) and (b, a) from one cell; g pure); the per-chain
ORDER is the rank-2 scatter's (its proof unchanged); every chain keeps its seed, kernel and operand order. Strict: bitwise the
walk under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, both lanes (raw bits, `BLADE_PRINT_BITS`). Licensed builds: v1 takes the
strict form for row actions too (the serial chain is an admissible association; no licensed competitor but the per-row
K-lane walk) -- recorded as such; `BLADE_ORBIT_ACTIONS=strict` restricts the form to strict builds for the A/B. A mixed
licence nest (a strict outer fold over a licensed inner fold) is REFUSED (D/5's `[1e16, -1e16, 1]` nest).

### Refusals (fail closed)

Two contracted axes in one stage (`T(i, j, k) * v(j) * w(k)`: ordered events need per-destination queues -- v2); AntisymIdx
(signed events -- v2); Hermitian; g reading S at a third coordinate or a second compact array; weights not rank-1 dense over
S's tag; per-row seeds; a mixed-licence nest; state > 2^20 chains; more than one channel (v1.1: K <= 4 after a measured 2x
over K single-channel passes, with the aggregate state and scratch bytes bounded -- review D/6); a DEFERRED S (P1-style
fusion, v2). Each refusal names the first failed condition in the plan record.

### Admission -- `orbitActionAdmissible` (every party through the shared write analysis)

The pool's producer by payload (stored, `storedArrayProof`, never assigned, no producer evaluation moves); comp / the fold
kernel / the seed W+F and total (IRTotality); every weight array stored, never assigned, DOMINATING the action (bound before
it); every binding between the pool's binding and the action by `intervalPayloadOf`: no write to S, a weight or a free
scalar, no provider effect; `callbackTable` for parallel bodies; the channel's output stays stored under its name. The
matrix in the plan record (`[fold-form v1] u: applied [orbit action (rank 2, extent n, 1 channel, row groups of G): own /
partner role terms; every row chain fed its source order from its seed]`) and in G73.

### Lanes / knob / records

C++ (`blade_orbit_scatter.hpp` `tri_action<G, B>`: per-cell own / partner terms into Zrow / Zcol blocks, lanes = distinct
chains, no pragma on chain loops; `CodeGenBinding` emits it at the action's binding) and LLVM (`EmitLlvm.tryOrbitAction`
beside `tryOrbitScatter`, the same APool admission, scalar chains + `<4 x double>` row-chain `fadd` without `reassoc`);
interpreter unchanged (the oracle). `OrbitFold.recogniseAction` beside `recogniseWalk`, `planAction` reusing `strictWalk`
/ IRTotality. Knob `BLADE_ORBIT_ACTIONS` unset/1 = on, `strict` = strict builds only, 0 = off, in `optimizerGates`
(opt-diff compares off vs on under REPRO -- make sure the form FIRES there; review D/6); CLAUDE.md row; docs/features.md;
G73 with the matrix, the role-term rule, the refusals, what is unproved (the Deduce residual-symmetry rule and the rank-3
tower are NOT this item: separate items with their own proofs).

### Tests

corpus `symmetry/140+` (next free: check `ls` -- 138 / 139 are W2-3's): the single-channel action at p = 13 with seeds
+-0 / 0.75 / -1.25 and bodies `x`, `x * v(j)`, `x * (v(j) - y(i))`, pinned against the interpreter and bitwise against the
walk-forced twin under REPRO (`BLADE_ORBIT_ACTIONS=0`); refusals (two contracted axes, antisym, a weight at a third
coordinate, a weight defined after the action, a mixed-licence nest, a deferred S); the three D/5 counterexample pins
(S01 = 2 / V = [3, 5]; an antisym value; the mixed nest). `blade test orbit-scatter` action leg (generated, both lanes, G in
{1, 3, 4, 8}, T = 1 only -- the action is serial in v1 -- raw bits vs the walk and the interpreter; routes asserted);
OptimizeTests decision cases per matrix row; `symmetry`, `orbit-scatter`, `optimize`, `cli`, `llvm symmetry`,
`interp symmetry`, `opt-diff symmetry`, `docs`, `corpus-wiring`.

### The kernel `pairaction` (bench/kernels/pairaction) -- review D/8: a fair executable contract

pairkernel's points and matern52 weights w STORED and NAMED (the S2 producer, `where comm`), R = 3 lcg right-hand sides
v_r (stored, named); z(i) = sum_j w(i, j); u_r(i) = sum_j w(i, j) v_r(j); y_r = u_r / z; checksum reading every stored
element of z, u_r, y_r; n = 2503 (non-power-of-two); input-only twin. Natural twin: the straightforward double loop over
the full symmetric matrix per RHS; expert twin: a packed-by-columns producer + per-column dspmv-style updates of all
channels (gfortran / ifx), serial; mt8 only as an OUTPUT-OWNED form (each thread owns rows of z / u_r; a shared-update
canonical loop with atomics or private merges is NOT strict and is not a fair repro twin -- say so in kernel.json; if no
strict mt8 twin exists, the kernel has no mt8 arm in repro). spec.md names the outputs and the exact chain order; strict
pins from an ordered scalar oracle; the Codex review of the kernel judges the twins before any timing. Directional, one
core: the v1 single-channel action vs the walk (z alone, u_0 alone), 11 interleaved, REPRO; the research's DROP for the
multi-channel form (>= 2x) is v1.1's experiment, not this item's.

### Scope

Not: multi-channel (v1.1), the rank-3 one-axis stage and `momenttower`, the Deduce rule, AntisymIdx, a deferred S, the
harness A/B. Report as the addendum says.
