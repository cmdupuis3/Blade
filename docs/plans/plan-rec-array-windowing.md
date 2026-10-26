# Plan: windowed (partially-materialized) recursive arrays

> **Status (2026-10-26): P2 v2a (fold fusion) BUILT at IR level, round 4 wave 2 (PM35), OPT-IN** (`BLADE_REC_FOLD=1`;
> default-on waits for one more converged use-census audit, gap G93: five review rounds each found
> a member of the trajectory use census). Not the
> surface splice section 9 sketches: `Optimize.windowRecFoldsModule` (rule `rec-fold-ring`, knob
> `BLADE_REC_FOLD`) runs after row-fold fusion, so slice-write fusion, the threaded step
> (rec-step-parallel) and the cell epilogues still see logical indices; it rings a trajectory whose
> later uses are all folds and moves each fold's state into the sweep. See CLAUDE.md's
> environment table for the admission. v2b (elementwise `|> compute` fusion), mixed fixed-row
> reads + folds, and P3/P4 remain open.
>
> **Status (2026-10-25): P1 PORTED, branch `claude/recarray-ring` (off master 4919eefe).**
> Block- and function-body `let rec`s window as specified in sections 3-4: classifier
> (`TypeCheckSupport.recArrayMaxLag` / `recArrayTailReads` / `recArrayWindowVerdict`), the
> ring + snapshot desugar (`TypeCheckInfer.recArrayLower`'s `window` argument; snapshots past
> the ring copied at a loop split), the `inferBlock` statement splice, and NAME dissolution.
> No diagnostic code: the decision is the `blade plan` record `rec-window v1`, pinned per
> corpus file by `blade test optimize` (`rec_window_corpus`), so no corpus WARN churn; the
> escape hatch is `BLADE_REC_WINDOW` (an optimizer gate). `while` guards and the freeze idiom
> demote; differentiated recursions need no special case (the AD expansion runs before the
> typechecker, and its tape reads demote by the tail scan; corpus `ad` passes). NOT ported:
> P2 consumer fusion, P3 module-level print replay (module scope demotes), P4 provider
> write terminals. Census 2026-10-25: 24 sites window across tests/examples/bench (examples/09
> 3, memfree-stress/011 800000 -> 3 rows, physics/47 6001 -> 3), 66 demote (52 whole uses).
> The history below is the August branch's.
>
> **Status (2026-10-11): BUILT ON AN UNMERGED BRANCH, NOT ON MASTER.** The code lives on
> `claude/recursive-array-materialization-381708` (2026-08-10, kept as the reference
> implementation). It predates the August file split (TypeCheck/CodeGen/IR groups), the
> reassignment of BL4017 (now "symmetry clause licenses nothing here"), the `while` guard
> (freeze + BL8010), reverse-mode AD through recurrences (which uses the full buffer as its
> tape), the freeze-idiom optimizer pass and `BLADE_PRINT`. A port is a re-implementation
> guided by this design: a windowing classifier must demote differentiated and
> `while`-guarded `let rec`s, mint a fresh diagnostic code, and replay prints under print
> selection. The "IMPLEMENTED" status below is the branch's, and its `file:line` cites are
> against the pre-split tree.

**Status: P0–P3 IMPLEMENTED — 2026-08-10, branch
claude/recursive-array-materialization-381708.** P1 (classifier + ring +
snapshots, block/function scope) landed as 19587f9; P2 (fold/broadcast
fusion) merged at 9576dbf with scope cuts recorded in
plan-rec-array-windowing-p2.md (rank-k full fold and general row apply
decline — physics/46's `superT` does not fuse yet); P3 (module-level
windowing + print replay) merged at 94a0c2c — 26 module-level corpus sites
flip to windowed with byte-identical printed output. P4 (provider write
terminals, §9b) DEFERRED per user direction. Measured: the 64 MB probe
trajectory peaks at 5.27 MB block-local and 5.28 MB module-level-printed
vs 68.1 MB full materialization, bit-identical values throughout.

**Original design follows (§3c's gate gained a `resident < N` clause in
implementation; §8's grep-the-C++ instrument was replaced by the BL4017
verdict warning — internal names do not survive lowering's rename).** Implements the storage policy formalism.md §7.5
already promises ("consumers that read only a trailing segment get a rolling
window", :844-857) and, as a stretch, the fused-consumer promise ("running
diagnostics ride the same sweep", :863-866). Neither exists in `src/` today:
every `let rec` allocates its full declared `N × trailing` buffer, frozen at
the typechecker desugar (`inferRecArray`, TypeCheck.fs:12465) before any
consumer is known. Verified 2026-08-10 by emitting C++ for four consumer
shapes (final-slice-only, function-local, reduce-over-trajectory,
`|> compute` on a derived slice) — all four materialize fully.

## 0. Vocabulary

- **Ring**: a K+1-slot buffer replacing the N-slot trajectory, where K is the
  recursion's maximum prefix lag. Slice `n` lives in slot `n mod (K+1)`.
- **Snapshot**: a single-slice buffer pinned to one compile-time ordinal `p`,
  copied out of the sweep when `n = p`. Serves post-definition reads
  `NAME(p)`.
- **Fusion**: splicing a consumer's per-slice work into the sweep loop so the
  trajectory never materializes at all (stretch goal).
- **Demotion**: falling back to today's full materialization. Demotion is
  always sound — windowing is a storage policy, never a semantics change.

## 1. What the corpus says (census 2026-08-10)

89 real `let rec` definitions across `tests/` + `examples/` (49 files):

| class | count | disposition |
|---|---:|---|
| WIN — block-local, bounded lag, final/trailing static reads | 23 | windows in v1 |
| PRINT — WIN-shaped but top-level ⇒ auto-printed | 35 | full in v1; converts via print replay (§7, P3) |
| ESCAPE — binding passed/returned bare | 12 | full, by design |
| DENSE — static-ordinal reads spanning ~all rows | 7 | full — **the classifier trap** |
| GRAD — feeds `ad.grad` (BPTT needs the trajectory) | 7 | full; grad lane is separate anyway (§6) |
| STREAM — sole consumer is an in-order fold | 4 | full in v1; fuses in v2 |
| CHKPT — scattered static ordinals (ex-09 `jtB(800)`,`jtB(1600)`) | 1 | **windows in v1** — snapshots cover it |

Lag depth is 1 at ~65/89 sites; max observed is 3 (`recursive-arrays/009`).
19 sites are zero-lag construction idioms (no `prefix` read at all). No site
in the corpus consumes `prefix` whole inside the slice — that demotion guard
needs a synthetic test, since real code never exerciseses it.

Payoff exemplars: `memfree-stress/011` (Idx<800000> trajectory, final read
only), `examples/09`'s `qsA1`/`qsA2`/`jtB` (the 2.26 GB residency program),
`physics/47`'s 6001-step trajectory. Also an ergonomics win: `sgs/006`,
`sgs/015`, `ml-equiv/017`, `examples/04/07/08` all *re-declare and re-run the
same recursion* once per extracted value to avoid materializing/printing it
whole — windowing removes the motivation for that idiom.

## 2. Semantic ground rules

1. **Type identity is untouched.** `NAME`'s declared type stays
   `Array<T like Idx<N>, ...>` conceptually; in windowed mode the name is
   *dissolved* (§4), so no type ever carries ring extents except the internal
   `__ring_` buffer's own anonymous head slot. Extents stay out of type
   identity; no BL3016/extent-baking seam is crossed.
2. **Implicit zero history survives.** formalism.md:838-843 commits to
   out-of-range lags reading zero under a K+1 buffer, and
   `recursive-arrays/009_zero_history.blade`'s docstring pins it ("any future
   storage change — the rolling window in particular — must keep these
   values"). The existing guard machinery already satisfies this for free:
   `guardsFor`/`guardWrap` (TypeCheck.fs:12568,12584) serve out-of-range
   reads from `zed`/`zeroSliceFor` *without touching the buffer*, and clamp
   the discarded branch's index in-bounds. The ring only changes the leading
   subscript of in-range reads.
3. **Ring residency invariant.** With lags ≤ K and ring size K+1: slice `idx`
   (written at step `idx`, overwritten at step `idx+K+1`) is read only at
   steps `n ≤ idx+K` — always before overwrite; and the write slot
   `n mod (K+1)` never collides with a read slot `(n-c) mod (K+1)` for
   `1 ≤ c ≤ K`. No barriers, no copies.
4. **Demotion is the default.** Any use the classifier can't prove
   window-safe ⇒ full materialization, silently correct. The decision is
   surfaced (§8) so tests can pin it both directions, but a wrong *demotion*
   costs memory, never correctness.

## 3. The classifier (storage-class decision)

Runs at the desugar site, over two inputs.

**(a) Internal — the slice expression.** Extend the existing
`rewritePrefixReads`/`guardsFor` walk to *record* what it already
pattern-matches (today it computes the lag constant `c` and throws it away):

- lag set `L = {c : prefix(n−c) reads}` → `K = max L` (0 if empty — the
  zero-lag construction idiom is just a 1-slot ring);
- `prefix` in any other position — bare argument, `reduce(prefix, …)`,
  dynamically indexed, `prefix(n+c)` — ⇒ `K = ∞` ⇒ **demote**. (This is
  guardsFor's "anything else" arm, now load-bearing; corpus never exercises
  it, so it ships with a synthetic test.)

**(b) External — every later use of `NAME`.** A syntactic scan of the
statement/declaration tail (plumbing in §5), recursing into all nested
expressions (later `let rec` seeds included — the continuation idiom is a
static-ordinal read and windows fine):

- `NAME(lit, rest…)` or `NAME((lit : T), rest…)` — literal possibly
  ascription-wrapped, both spellings pervasive in the corpus — ⇒ add `lit`
  to the snapshot set `R`;
- **anything else** — bare `NAME`, dynamic index, assign target, alias
  `let x = NAME`, range/segment read — ⇒ **demote**;
- module scope ⇒ **demote** in v1: auto-print (`genPrintStatements`,
  CodeGen.fs:17754) observes every top-level binding whole and has no
  suppression hook (§7).

**(c) The coverage gate — mandatory.** "All reads are static literals" is
*not* sufficient: 7 corpus sites (DENSE) read literal ordinals spanning
essentially the whole trajectory (`ml-e2e`'s `wtraj` reads every one of its
31 rows). Window only when it actually shrinks storage:

```
window ⇔ K finite ∧ tail-scan clean ∧ (K + 1 + |R|) < N ∧ (K + 1 + |R|) ≤ max(8, N / 4)
```

(threshold tunable; the point is gating on *span*, not literalness). The
strict `< N` clause matters independently of the ratio clause: at small N a
resident set equal to the buffer (math/057: ring 2 + 1 snapshot vs a 3-cell
buffer) satisfies `≤ max(8, …)` while buying nothing and costing a snapshot
copy — implemented in `recArrayWindowVerdict`.

Classifier output: `Windowed(K, R)` or `Full(reason)` — reason from a small
enum (module-print / bare-escape / dynamic-read / unbounded-lag /
dense-coverage / grad) for the diagnostic.

## 4. Mechanism v1: ring + snapshots

In `Windowed(K, R)` mode the desugar emits, in place of today's block:

```
let mut __ring_NAME  : Array<elemT like Idx<K+1>, trailing…> = zeros   // K+1 slots
let __snap_<p>_NAME  : Array<elemT like trailing…> = zeros             // one per p ∈ R
<seed write → ring slot 0; if 0 ∈ R, also → __snap_0>
for n in start..N:
    <lag lets: ring reads at (clamp(n−c)) mod (K+1), guards unchanged>
    <slice compute; write → ring slot n mod (K+1)>
    <for p ∈ R: if n = p, copy slice → __snap_p>
// NO binding for NAME is emitted
<tail, with every NAME(p, rest…) rewritten to __snap_<p>_NAME(rest…)>
```

Decisions and their reasons:

- **`NAME` dissolves.** Since every legal read was rewritten to a snapshot,
  binding `NAME` to anything would be dead weight (and binding it at ring
  extents would lie about its declared type). Dropping it buys a structural
  fail-safe: **if the classifier and the rewriter ever disagree, the leftover
  use of `NAME` is an unbound-variable compile error, not silent corruption.**
- **Statement-level splice, not expression-level.** The desugar result is a
  statement *sequence* (ring/snapshot lets + loop) replacing the single
  `StmtLet`, plus a rewritten tail. This drops out of the same plumbing §5
  needs anyway. Snapshot names carry a uniquifier (two same-named `let rec`s
  in sibling scopes must not collide).
- **Slot arithmetic**: `n mod (K+1)` via `%` on the int64 ordinal (K=1 ⇒
  alternation; a rotating counter is a later micro-opt). The clamp-then-mod
  order keeps the discarded guard branch in-bounds (ground rule 2).
- **Zero-fill**: ring-sized — `(K+1+|R|) × trailing` cells, which falls under
  `zerosOver`'s 4096-cell threshold in practically every case, taking the
  plain nested-literal branch (TypeCheck.fs:12520-12522) and sidestepping the
  `method_for` former path entirely (seam map §6: that former is a blessed
  let-RHS consumed by the combinator-nest emitter, never the lift pass — but
  we don't even get there).
- **Type surgery**: rebuild the buffer annotation by replacing the head slot
  — the inline idiom `TyArray (elemT, newSlots)` already used at
  TypeCheck.fs:12671, `mkArrayLike { at with IndexTypes = … }` (IR.fs:776) at
  the IR level. Snapshot types are the trailing slice type, exactly what
  `NAME(p)` produced before, so the rewritten tail typechecks identically
  (tags and units preserved).
- **BL4003 trap** (seam map §7): `isSynthesizedBuffer`'s `__`-gate
  (TypeCheck.fs:2402-2405) suppresses only the *warning* branch of
  `checkArrayIndexTags`; the tag-mismatch *error* (:2419-2422) still fires on
  named-tag conflicts. The ring's head slot must be an **anonymous**
  `Idx<K+1>` and the mod-subscripts plain ints — same discipline as today's
  `stepIdxE` writes. P0's probe (§9) confirms this before any code is built
  on it.
- **Both lanes for free**: the interpreter runs on lowered IR and never sees
  `ExprRecArray` (`Lowering.fs` has zero RecArray references; Interp consumes
  `Blade.IR` — seam map §2), so the desugar change covers compiled and
  interpreted execution identically, and the interp-differential suite is a
  ready-made equivalence oracle.

Memory arithmetic for the exemplars: `memfree-stress/011` drops
800,000 → 2 resident slices; ex-09's `jtB` drops 1601 → 2 ring + 2 snapshots.

## 5. Plumbing: handing the tail to the desugar

`inferRecArray` has exactly one caller: `inferLetBindingValue`
(TypeCheck.fs:12271, dispatch at :12301-12303). Neither of the two paths that
reach it currently sees the statement tail:

- **Block level** — `inferBlock`'s `for stmt in stmts do` loop
  (TypeCheck.fs:13102, mutable `curEnv`/`typedStmts`): restructure to
  `head :: tail` recursion (or indexed iteration) so the `StmtLet{ExprRecArray}`
  arm can (a) hand `tail` to the classifier and (b) **replace** the current
  statement + tail with the spliced sequence + rewritten tail. This is the
  only restructuring v1 needs.
- **Module level** — `checkModule`'s `for d in modul.Decls` (:16235) has the
  same shape. v1 demotes module scope anyway (auto-print), so the decl-loop
  restructuring lands with P3, where print replay (§7) gives module-level
  windowing its reason to exist — the tail scan there feeds both the
  snapshot set and the replay decision.
- The expression-form `let … in body` path (`inferLetBinding`, :12851)
  already carries its continuation as `body` — trivial when needed.

The tail rewrite itself is `Unfold.mapExprPre` substitution (the same
machinery `rewritePrefixReads` uses), applied to each tail statement.

## 6. What stays full, and why that's correct

- **Module-level bindings** (35/89): auto-printed whole; no suppression
  mechanism exists (seam map §3). See §7 for the follow-on.
- **grad/BPTT** (7): entirely orthogonal — `Grad.expandRecArray`
  (Grad.fs:455) is a *pre-typecheck surface rewrite* that emits its own
  full-extent buffer and consumes the trajectory in a triangular adjoint
  accumulation; `inferRecArray` never sees those sites. Windowing there means
  checkpointed BPTT — a separate, later design.
- **ESCAPE / DENSE / delay-embedding / dynamic reads**: genuinely need the
  data. `physics/44`'s `halo<Idx<1500>, [-12,-24]>` delay embedding and
  `ppl`'s `autocorr`/`ess` (multi-offset reads across the whole chain, and
  consumers elaborated *later* than the chain's synthesis — windowing there
  requires cross-declaration reachability the elaborator doesn't have) are
  the canonical examples. PPL chains stay materialized.
- The AB3 ladder in ex-09 already hand-rolls a K=2 window by `stack()`ing
  lagged tendencies into each row — evidence of demand, and those sites still
  window as lag-1 recursions over the stacked state.

## 7. Print as a force point: slice-wise print replay (P3)

The census's sharpest finding: the single biggest blocker (35/89 sites) is
the compile-print-exit convention, not semantics. The resolution follows
from taking laziness seriously: **the auto-print is just another in-order
consumer, and a pure recursion can be *evaluated at its consumer* instead of
ahead of it.** Print is a force point, exactly like `reduce(deferred, op)`
and the planned `z.write` terminal (StreamingIONotes.md P5).

**Mechanism — replay at the print slot.** `genPrintStatements`
(CodeGen.fs:17924) walks `modul.Bindings` in declaration order and already
special-cases two kinds of binding with no materialized array (deferred-not-
forced combinators print nothing, :17946; streamed provider reads print
nothing, :17949-17951). Print replay is a third kind, except it prints
*everything*: a module-level `let rec` classified `Windowed-except-print`
emits no trajectory buffer in the compute phase — only the ring, plus
snapshots if other consumers need them — and registers its sweep recipe in a
side-channel map on `IRModule` (the `ProviderWrites` /
`forcedDeferredIdsCell` pattern). At the binding's slot in the print
sequence, codegen emits the sweep *re-run with the per-slice print inlined*:
compute slice `n` into the ring, print it, advance. Output text is
byte-identical — same values (the serial sweep is deterministic, so replay
reproduces it bitwise; no reassociation anywhere), same order (the replay
sits exactly where the buffer's print sat), same formatting — so **every
existing EXPECT pins passes unchanged while the buffer drops from N to K+1
slices.** That converts the census's ~26% window-eligible to ~65% without
touching a single test expectation.

Scheduling, two cases:

- **Print is the sole consumer** (`001_scalar_lcg`, `004`, `009` ×5, most of
  `memfree/`): the compute phase emits *nothing* for the binding; the single
  sweep runs at the print slot. Total work unchanged, memory O(K·slice).
- **Mixed consumers** (a snapshot read feeding other compute-phase bindings,
  plus the print): the compute phase runs the windowed sweep once to fill
  snapshots; the print slot runs it again for the slices. 2× sweep compute
  for O(N)→O(K) memory — precisely the trade users already hand-roll by
  re-declaring the same recursion per extracted value (census idiom #11),
  now automatic and scoped to print. If the recompute is unwanted, the
  binding demotes (it's a policy knob, not a correctness question).

Two honest caveats, neither blocking:

- **Timing attribution**: the `completed in` line is emitted between the
  compute phase and the print phase, so a sole-consumer replayed sweep runs
  *after* the timestamp. Corpus EXPECTs match values, not times; the timing
  differential block needs a look before P3 lands, and the artifact gets
  documented.
- **Replay must be a closed recipe**: the sweep re-run at print time reads
  the same inputs the compute-phase sweep would have — fine for anything
  bound earlier in `main` (module bindings all live to exit by design), but
  the recipe must not capture block-locals that went out of scope. At module
  level (the only place print replay applies) every free variable of the
  desugared sweep is a module binding, so this holds by construction; the
  implementation asserts it.

**Cheaper interim options, still worth having**: (a) v1 documents the
block-wrap idiom (`let x = { let rec traj: …; traj(final) }`) — users
already write it to dodge printing, and with windowing it becomes the memory
answer too; (b) a print-suppression hook (attribute or leading-underscore
convention) in `isPrintable` remains a trivial opt-out for code that wants
neither the buffer nor the output.

## 8. Surfacing the decision (no silent storage changes)

Every `let rec` gets a pinnable verdict, in the spirit of the omp census
marker invariant:

- an **info diagnostic** from the classifier: `recursive array 'traj':
  windowed, K=1, slots=2, snapshots=[9999]` or `recursive array 'traj': full
  (<reason enum>)` — captured by the WARN-pin harness so corpus tests pin
  both directions, and a fixed classifier later must retire its dead pins in
  the same change;
- the emitted C++ carries the `__ring_`/`__snap_` names and K+1 extents, so
  the "did the optimization survive" grep recipe works on the generated
  source too.

## 9. Stretch: consumer fusion (v2)

The census found 4 STREAM sites (`ml-e2e` `errs` ×2, `physics/46` `superT`,
`physics/47` `pw`) plus the reduce-shaped diagnostics idiom. Two tiers:

- **v2a — fold fusion** (implements formalism :863-866 directly): if the
  *sole* use of `NAME` is the next statement and it is a
  reduce-over-leading-axis (or the default innermost-axis partial fold — both
  of `inferReduce`'s relevant routes produce per-slice work), splice the
  accumulator / per-slice output into the sweep. The trajectory rings; the
  fold's output (a scalar, or an N-vector of per-step scalars — a loss trace)
  is what materializes. Enumeration order *is* the defined fold order for the
  serial sweep, so no comm license question arises; the time loop was never
  parallel.
- **v2b — elementwise `|> compute` fusion** (the user-named stretch case): if
  the sole use is `let s = <elementwise expr over NAME> |> compute` and
  `NAME` is dead afterwards, the consumer's leading-axis access is
  index-aligned by construction (`*` desugars to zip), so slice `n` of the
  *output* is computable inside the sweep from ring contents. The output
  materializes — it is the demanded deliverable — and the trajectory never
  does. Consumers mixing across the leading axis (time-axis fft, sort,
  transpose-then-use) fail the alignment check and demote, correctly.

Both tiers reuse §5's statement splice; fusion is "the consumer's per-slice
body becomes extra statements in the sweep". Sequencing v2a before v2b: the
accumulator splice is a strict subset of the machinery.

**The unifying frame** (which §7's print replay also instantiates): a
recursive array is a lazy producer, and every in-order consumer — fold,
elementwise compute, auto-print, provider write — is a *force point* that
can pull slices through the ring instead of reading a materialized buffer.
The classifier's job is recognizing force-point-only consumption; the
mechanism is always "per-slice consumer work rides the sweep (or a replay of
it)".

## 9b. Streaming external writes (P4): the out-of-core tier

`alias.write("path", A)` exists today for all three providers
(`GenWriteVar` in the `ProviderSpec` seam, ProviderRegistry.fs:69; the
deferred-write path at CodeGen.fs:13920 flattens a *materialized* source —
StreamingIONotes.md's v1 scoping decision, "writes stream FROM that buffer,
post-hoc"). StreamingIONotes P5 already plans the write-terminal form for
pipelines: `z.write("out", <unforced pipeline>)`, the write *is* the force
point, the output never materializes. A recursive array is the missing
producer for that terminal:

```blade
let rec traj: Array<Float like T, Y, X> = …    // classified: windowed
z.write("traj", traj)                          // sole whole-array consumer
```

- The write consumer is in-order by construction (leading-axis-major store
  order), so it passes the same alignment check as v2b fusion: each slice
  is written as the sweep evicts it. RAM holds the ring; disk holds the
  trajectory. **This is the genuinely out-of-core case** — `Idx<10⁷>`
  trajectories whose full buffer would not fit in memory become writable.
- **Chunk alignment (zarr)**: with leading-axis chunk extent `c`, slices
  buffer into a `c`-slice staging block flushed per chunk boundary —
  resident cost `max(K+1, c)` slices, still O(1) in N. Choosing the store's
  chunking to match (or defaulting `c` small for trajectory writes) is a
  provider-side knob. CSV (rank ≤ 2, sequential rows) and NetCDF
  (`nc_put_vara` per slice range) stream trivially.
- **Classifier integration**: `alias.write(path, NAME)` is a new arm in the
  tail scan — today it would demote as a bare-identifier use; P4 teaches it
  to classify as a force-point consumer (window-compatible) when it is the
  sole whole-array use. Combined shapes compose: snapshots for static reads
  + streaming write + print replay can all hang off one sweep.
- **MPI note**: out of scope here; StreamingIONotes P6's ownership model is
  the eventual meeting point.

Farther out (v3+, listed for orientation, not commitment): halo-along-time
consumers with bounded offsets (widen the ring to cover the stencil), grad
checkpointing, provider-*read*-seeded recurrences (a streamed input feeding
a windowed recursion — full streaming pipelines end to end).

## 10. Verification

- **P0 probe** (before building anything): hand-write the ring shape as a
  plain `.blade` program — `let mut ring: Array<Float like Idx<2>, X>` +
  mod-subscript writes in a loop — and `emit`/`run` it to confirm the BL4003
  anonymous-head-slot assumption, unit flow through slice writes, and codegen
  of `%`-subscripts, before the desugar depends on them.
- **Equivalence**: the interp-differential and diff-oracle blocks, scoped to
  the touched families (`recursive-arrays`, `memfree`, `spectra`, `sgs`,
  `ml-equiv`, examples 04/07/08/09) — per the A/B instruments memory, scope
  the value-level A/B to the change's guard rather than the 10-hour full
  sweep. Full `blade test` green before/after each phase.
- **New corpus, window-fires side** (each pinning the info diagnostic and,
  where cheap, the `__ring_` extents in emitted C++): K=1 final-read
  block-local; K=3 multi-lag mirroring `009_zero_history`'s off-the-start
  reads **under the ring** (the one place a wrap-around bug would show);
  scattered snapshots (the ex-09 `jtB` shape); continuation seeding from a
  snapshot; nested rec (inner windowed, outer full); zero-lag construction
  idiom.
- **New corpus, demotion side** (pinning `full (<reason>)`): unbounded lag
  (`reduce(prefix, …)` — the guard real code never exercises), dynamic
  index, bare escape, dense coverage, module-level.
- **Memory truth**: `blade run --memcheck` A/B on `memfree-stress/011`
  (expect outstanding to drop by ~6.4 MB → ring+snapshot KBs) and on a
  function-local loop driver; ex-09 stays as the module-level control
  (expected *unchanged* in v1 — an honest demotion, reported as such).
- **memfree family**: untouched in v1 (all top-level ⇒ Full path unchanged);
  assert byte-identical emitted C++ for them via the strip-comments-and-diff
  recipe.

## 11. Sequencing

| phase | contents | risk retired |
|---|---|---|
| P0 | ring-shape probe; classifier + tail scan; info diagnostic **only** (no storage change); `inferBlock` tail plumbing | BL4003/units/codegen assumptions; classifier correctness pinned corpus-wide before anything moves |
| P1 | ring + snapshots for block/function-local; `NAME` dissolution + tail rewrite; window/demotion corpus; memcheck A/B | the actual feature |
| P2 | v2a fold fusion, then v2b compute fusion | the stretch goal |
| P3 | print replay (§7): `checkModule` decl-loop plumbing, replay side-channel, `genPrintStatements` arm; EXPECT corpus must pass byte-identical; timing-block audit | converts the 35 PRINT sites (~26% → ~65% window-eligible) with zero test churn |
| P4 | provider write terminals (§9b): tail-scan arm, per-slice/per-chunk flush in the sweep, zarr chunk staging | the out-of-core tier |
| P5+ | print suppression hook; halo-ring; grad checkpointing; streamed-read-seeded recurrences | options, each with its own doc |

Independent quick win, separate change: the O(N × trailing) zero pre-fill of
the *full* buffer appears dead (every in-range slot is written before read;
out-of-range reads never touch the buffer) — ~2.26 GB of stores in ex-09.
Needs its own audit; do not bundle.

**Interaction with the function-local leak fix** (9e5e6c0 + d3e005d, merged
to master 2026-08-10: in-frame copy elision + materializing returned
interior views): complementary, not conflicting. Windowed sites stop
producing the giant staging buffer at all (a returned snapshot is a small
fresh array — the leak shape vanishes), and Full-class function-local
recursions keep the merged fix. This plan builds on top of it; the new
`memfree/021_func_local_rec_returned_row.blade` corpus test must stay green
through every phase.
