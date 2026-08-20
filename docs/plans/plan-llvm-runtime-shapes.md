# LLVM lane: ragged and grouped shapes (RaggedIdx, EnumIdx/group_by)

Status: RAGGED LANDED — Phases 0-2 for the RaggedIdx family (plus the ragged
function-parameter ABI) implemented 2026-08-20 on `feat/llvm-ragged`; group_by
phases (4-5) not started. Revised 2026-08-19 after review: the first draft
phased static-ragged first; that was wrong and the revision inverted it. See §0.

Landed shape (src/EmitLlvm.fs): `GRagged of rows * RaggedTable` spans two axes;
`RaggedTable = RtStatic of offsets[] * constant-global sym | RtDynamic of ptr`
— one addressing/loop path, the table operand is the only difference (the
user's two-lanes-by-IR call: IxKRaggedInline literals and DepIdx are RtStatic,
function params arrive RtDynamic). `GDynDense of len-operand` is the peeled
row. Ragged params cross as pool + offsets-table pointer (two args). Covers:
literals (incl. DepIdx), curried/tuple reads, row views, consuming +
elementwise peels, row reduce/extents, chained maps, prints (nested rank-2 /
flat rows), memfree let-rec temps. Refused by name: ragged returns,
multi-operand ragged, group kinds. All ~28 ragged corpus files byte-identical
to the g++ lane; `blade test llvm index-types` coverage 47.0% (78/166
comparable) with zero ragged entries left in the refusal histogram.
Pre-existing (not this change): memfree/007 print drift, tracked separately.

Goal: the LLVM lane (`BLADE_LLVM=1`, `src/EmitLlvm.fs`) emits the ragged/grouped
family natively and **beats the g++ lane** on those shapes, instead of refusing
and falling back. Baseline at plan time: `blade test llvm` = 351 passed, 0
failed, 364 skipped; every ragged/grouped file skips with a named refusal
(`requireArray` wording, or the `IRGroupKeys` catch-all).

---

## 0. The correction that reshaped this plan

The first draft rested on "RaggedIdx as it exists today is statically shaped —
every producer has row lengths known at emit time." That is true of **current
codegen** and false of **the language**, and building on it would have made the
dynamic case a retrofit onto the wrong canonical form.

What the compiler actually says — `src/IndexTypeValidator.fs:105-128`:

> True iff `ty` is KNOWN to be statically-evaluable; defaults to false
> (**runtime-evaluable is the default, static the special case**). … Runtime:
> RaggedIdx, RaggedIdxOpaque, CompoundIdx (structurally require runtime data).
> DepIdx: static iff both outer and body are static.

```fsharp
| TyRaggedIdx _ | TyRaggedIdxOpaque | TyCompoundIdx _ | TySparseIdx _ -> false
| TyDepIdx (outer, _, body) -> isKnownStatic env outer && isKnownStatic env body
```

So the type system already draws the line: **DepIdx is the extents-known-ahead
tool; RaggedIdx is the runtime one.** Pinned by
`tests/corpus/inference-probes/013` (BL4003 on `static function f() ->
RaggedIdx<lens>`).

Three consequences:

**C1 — the gap is a missing producer, not a static container.** The g++ lane's
*consumption* half is already fully dynamic: `Ragged<T>` is a runtime CSR
descriptor and every peel reads `lens[__g]` as a memory load
(`src/CodeGenCuda.fs:2253-2254` even documents the resulting non-affine bound as
why `collapse(2)` is refused). It would work unchanged against a runtime-filled
table. Nothing can fill one: the only two `Ragged<T>` construction sites are the
literal path (`src/CodeGenLoopNest.fs:3800-3817`, `static constexpr` lens) and
the DepIdx path (`:3763-3778`, refuses a non-statically-evaluable formula at
`:3732`). `group_by`'s CSR is the sole runtime one, and it is not even
RaggedIdx-typed (`IxKGroupMember` is excluded from `isRaggedFamilyKind`,
`src/Types.fs:336-339`).

**C2 — `IRRaggedLookup` is aspirational.** Its doc comment (`src/IR.fs:192-198`)
describes emitting a runtime lengths lookup. It has **zero consumers** in
codegen and the interpreter; routing happens off the `IxKind` tag and lengths
come from walking the literal. A closed `RaggedIdx<lens>` annotation is inert
today — see §6 for the correctness hole that follows.

**C3 — dynamic-first is also the cheaper build order,** which is the decisive
argument and is independent of C1/C2. See §2.

---

## 1. Why this beats the g++ lane

| g++ lane cost | LLVM lane answer |
|---|---|
| `Ragged<T>` carries an Iliffe `T**` row table **and** CSR offsets (`src/cpp/nested_array_types.hpp:96-115`); every read is two dependent loads, and the literal path builds the table in a runtime loop from static data (`CodeGenLoopNest.fs:3808-3814`) | CSR only: `pool[offsets[i] + j]`, one load. `rowView` (`EmitLlvm.fs:1380-1395`) already produces the row base as one `getelementptr` with no skeleton |
| Elementwise ragged map allocates twice per call (`new T[total]` + `new T*[n]`, `CodeGenCuda.fs:2246-2247`) | One `blade_alloc_cells`, no table |
| Peel output heap-allocates a `size_t[1]` extents table per result (`CodeGenCuda.fs:2365-2377`) | No descriptor exists; static extents bake into GEPs |
| `group_by` materializes **ngroups separate `new T[sz]` blocks** plus a gather copy — so expensive that gather-elision is special-cased (`CodeGenBinding.fs:2028-2045`) | One CSR pool, group-contiguous, one allocation; elision becomes unnecessary rather than special-cased |
| `offsets[g+1]-offsets[g]` recomputed per use, and again in `extents(gk)` (`CodeGenBinding.fs:2118-2120`) | Hoisted once per row into SSA (§5.b) |
| Row starts have no alignment guarantee | **Padded CSR** gives every row a 64-byte-aligned start (§3, D6) — a win the g++ lane does not have and cannot cheaply get |

Two capability wins beyond speed, both noted and neither scheduled: the g++
lane has **no** multi-operand ragged path at all (`#error` fence,
`CodeGenCuda.fs:2645-2646`), yet a map output *shares* its parent's offsets, so
same-table co-iteration is sound and emittable here. And contiguous CSR makes
the "gather rows into a staging buffer first" forward transform
(`src/cpp/nested_array_utilities.hpp:67-71`) a **no-op** rather than work.

---

## 2. The build order, and why static-first was wrong

The reviewer's hypothesis was that emitting the offsets table as an LLVM
`constant` global would let LLVM's own optimizer recover the static case's
literal trip counts, making one emitter cover both branches for free.

**That hypothesis is false in the shape that matters, and the conclusion
survives anyway for a stronger reason.**

Why it fails: for a nested outer-row/inner-element loop, the inner bound is
`load @off[%g+1] - load @off[%g]` where `%g` is the *outer* IV — not a constant
expression, so `ConstantFoldLoadFromConstPtr` never fires. The fold only
happens if the outer loop is fully unrolled first, and LLVM's full-unroll cost
model declines on non-innermost loops once the loop is anything but tiny.

**Measured** (§8; clang 22.1.8, `-O3 -march=native`, hand-written CSR nests
with non-affine row lengths, instruction counts after stripping directives):

| nrows | A: `constant` table | B: opaque table | C: hand-unrolled literal |
|---|---|---|---|
| 4 | 24 | 116 | 24 |
| 64 | 40 | 38 | 403 |
| 512 | 40 | 38 | 3203 |

At `nrows = 4`, A and C are **byte-identical machine code** — the table is
dead-eliminated entirely. At 64 and 512 the outer loop is not unrolled at all
and A's code section is byte-identical to its own 64-row version: constant-ness
does not buy the unroll. So the static specialization's full payoff exists only
at single-digit row counts, and C shows always-unrolling is actively harmful at
scale (3203 instructions versus a tight 40-instruction loop).

**One real, size-independent win from `constant` did show up**: at every size A
issues **one** offsets load per outer iteration where B issues two, because
`constant` proves the table cannot alias the output buffer, letting LLVM reuse
the previous iteration's `hi` as this iteration's `lo`. Free, and worth taking.

Why dynamic-first wins regardless:

- **The constant branch is strictly *less* emitter work than the dynamic
  branch** — no `allocPool`, no fill loop, no scope-exit free, no
  `llvm.assume`. Build dynamic first and staticness arrives as a *subtraction*.
  Build static first and literals get baked into `Grp`, `allocPool`,
  `storageOffset`, and `.Extents`, making the generalization exactly the
  retrofit we're trying to avoid.
- **The static advantage is self-limiting.** Literal inner trip counts require
  unrolling the outer loop by `nrows`. That is affordable only when `nrows` is
  small — which is when the total work, and therefore the win, is smallest. At
  streaming scale (`nrows` in the hundreds) it is not an option at all.
- **The variable-trip inner loop is not a new risk in this lane; it is the
  shape of its current win.** `emitBrick`'s on-diagonal arm already emits a
  register-bounded inner loop inside an outer loop
  (`EmitLlvm.fs:1502-1505`), on the path measured to beat the g++ lane at rank 4.
- **The g++ lane already validated the structural half**: it resolves a
  "lengths source" (`gk__offsets[g+1]-gk__offsets[g]` for grouped,
  `arr.lens[__g]` for ragged) and reaches **one** loop shape from both
  (`CodeGenCuda.fs:2131-2178`).

So: **dynamic table is the form; staticness is an additive decoration.**

---

## 3. Design decisions

**D1 — flat contiguous CSR, no Iliffe table, both branches.** One data pool +
one offsets table (+ perm for grouped). Offsets/perm are ordinary pools from
`blade_alloc_cells`, entering the existing `PoolScope` machinery with no new
lifetime concept.

**D2 — offsets are the stored form; lens is a derived accessor, never a second
table.** Offsets are what addressing needs and `offsets[nrows]` is the
allocation size. `rowLenOf` emits `sub (off[i+1], off[i])`, hoisted once per
row; it also answers `extents(row)`. The g++ `Ragged<T>` carries both and it
bought nothing but a consistency burden and ownership confusion
(`nested_array_utilities.hpp:506-516`). A producer that naturally yields counts
(a CF contiguous-ragged file variable does) gets prefix-summed at construction,
exactly as `CodeGenBinding.fs:407-409` already does for grouped.

**D3 — the shape accessor API is an enforced invariant, not a convention.**
`GRagged`'s fields are unreadable outside the accessor module. Every addressing
site goes through `offsetOfRow` / `rowLenOf` / `nrowsOf` / `totalCellsOf`, each
folding to a literal when the table is emit-time known and emitting a `load`
when it isn't. If this is only a convention, sites accumulate that destructure
the constant table and the static decoration stops being a subtraction. **This
is the single commitment that decides whether a future streaming read is cheap
or a rewrite.**

**D4 — do NOT make `Grp`'s extent an operand everywhere.** Measured blast
radius: of the `.Extents` uses, only **four** consume the `int64` value
(`EmitLlvm.fs:1950`, `:2590`, `:3098`, `:3598`); thirteen are `List.length`,
i.e. rank. Making extents operands module-wide would destroy three textually
pinned emission properties — `i64Add`'s literal-zero folding (`:1141-1142`),
`emitRowBase2`'s constant `2n+1-2·strict` (`:1152`), and `storageOffset`'s
Horner chain (`:1189`) — for zero ragged benefit. Add two variants instead
(§4).

**D5 — static regimes stay pure `.ll`; only dynamic discovery goes to the
shim.** Positional (`Idx<N>`) grouping is count/prefix-sum/scatter with static
`ngroups`. `EnumIdx` int keys: the value list is compile-time, so bucket lookup
is an emitted `switch` over ≤ |values| constants. Only unannotated-dynamic and
multi-key discovery becomes a shim call — O(n) once, where the lane's lack of
LTO (`Build.fs:1046`, `:1102` — no `-flto`, shim calls are opaque) doesn't
matter. **Never a shim call on a per-element path.** Do not add LTO.

**D6 — padded CSR: round each row start up to a multiple of 8 elements.** Rows
otherwise start 8-byte aligned, so the vectorizer emits unaligned moves and per-row
peeling — a regression against every other array in this lane, where
`blade_alloc_cells` returns 64-byte-aligned memory as a hard contract
(`src/cpp/blade_llvm_shim.c:60-73`). Padding is invisible to addressing (the
table absorbs it) and available to both branches. Cost: ≤7 cells/row, plus one
interaction that **must land in the same change** — see §7.

**D7 — emitter allocates, callee fills; two-phase (size, then fill) calls.**
No shim or provider ever returns a pointer it allocated. Any producer that
discovers its own size uses:

```llvm
%n_slot = alloca i64 ; %tot_slot = alloca i64
call void @blade_X_discover(<args>, ptr %n_slot, ptr %tot_slot)   ; sizes only
%tot  = load i64, ptr %tot_slot
%off  = call ptr @blade_alloc_cells(i64 %n_p1, i64 8)             ; PoolScope-tracked
%pool = call ptr @blade_alloc_cells(i64 %tot,  i64 8)
call void @blade_X_build(<args>, ptr %off, ptr %pool)             ; fill only
```

This shape is *required anyway* by dynamic `group_keys` (offsets cannot be
sized before discovery), so it costs nothing now and makes any future producer
a third instance of an existing pattern.

**D8 — land incrementally behind the refusal.** Refusal is whole-program,
silent, and free (`CliCommands.fs:314-326`). Refusals must be raised *before*
shared layers (`applyToArr` pattern, `EmitLlvm.fs:2704-2712`) — an exception
from a shared layer escapes `tryEmitProgram` and takes the harness with it.

---

## 4. The value-model change (contained)

```fsharp
type private Grp =
    | GDense of int64
    | GSym of int * int64
    | GAnti of int * int64
    /// RANK 2. A ragged pair whose row base is a TABLE LOOKUP instead of
    /// emitRowBase2's closed form. Rows is static (every ragged outer axis the
    /// front end builds is a plain Idx<n> or a group-outer count); the INNER
    /// extent is per-row and lives in the table.
    | GRagged of rows: int64 * off: OffTable
    /// RANK 1, extent as an OPERAND. Reached ONLY as the residual of a ragged
    /// row peel — a peeled row's length is known only at runtime.
    | GDynDense of len: string

/// The table's ADDRESS — a ptr operand in both branches: a global symbol, or a
/// register from allocPool. `Known` is read ONLY to select decorations (§5),
/// never to select addressing or loop shape.
and private OffTable = { Sym: string; Known: RaggedKnown }
and private RaggedKnown = RkUnknown | RkKnown of int64[]
```

`GDynDense` is unavoidable: peeling a ragged row *must* yield a rank-1 array
with a runtime extent, and that value has to be representable.

Site map (all `src/EmitLlvm.fs` unless noted):

| Site | Change |
|---|---|
| `groupOfIndexType :1053-1064` | the `IxKind <> IxKPlain` guard at `:1054` is where ragged dies today. New arms: `IxKRaggedInline` → `RkKnown`, `IxKRagged`/`IxKRaggedOpaque` → `RkUnknown` |
| `grpRank :501` | `GRagged -> 2`, `GDynDense -> 1` |
| `grpExtent :502`, `grpCells :513-517`, `shapeCells :519` | **refuse** on the new variants; add operand-valued twins (`grpCellsVal`, `shapeCellsVal`) so callers are forced to the operand-aware path |
| `allocPool :1219` | signature takes `(n: string)`; existing callers pass `string n` → **no emitted-text change**, goldens hold |
| `grpOffset :1160-1165` | one arm: `GRagged` → `i64Add c (loadOff c off i) p` — the direct analogue of the `GSym/GAnti` arm at `:1163-1164`. `GDynDense` → `i.Reg` |
| `storageOffset :1172-1191` | the `mul acc, <cells>` at `:1189` routes through `grpCellsVal` |
| `splitGroupsAt :1366-1374` | relax the mid-group refusal **for `GRagged` only**: `[i]` of a ragged group is legal and yields `GDynDense`. The wording at `:1369` stays right for `GSym`/`GAnti` |
| `rowView :1380-1395` | `GRagged` lead offset **is** `off[i]`, `block = 1` → takes the `:1390` short path unchanged |
| `emitShapeNest :1553-1564` | one arm: outer `emitRowsLoop` over rows, inner `emitCountedLoopTo` with the loaded length |
| `emitReduce :3096-3098` | `GDynDense` arm via `emitCountedLoopTo` (`:1093`) — **the primitive already takes an operand** |
| `ArrVal :569` | add `member this.Rank`; migrate the 13 `List.length a.Extents` sites (mechanical, no emission change); `.Extents` then refuses on new variants |
| `shimTable :839-849` | one row: `blade_ragged_offsets` (prefix-sum + overflow panic), group `grpShimAlloc` |

---

## 5. Decorations (the only thing `Known` selects)

1. **Provenance.** `RkKnown` → a `private unnamed_addr constant [n+1 x i64]`
   global (the `stringGlobal :926-941` construct, retyped). `RkUnknown` →
   `allocPool` + fill. Only `OffTable.Sym` differs downstream. Note `constant`
   in LLVM means *never written by anyone*, so passing the table to a shim does
   not defeat folding.
2. **Metadata.** `RkKnown` → `!range` on length loads (true min/max known),
   feeding SCEV a bounded trip count. `RkUnknown` → `!invariant.load`, which
   lets GVN/LICM CSE and hoist the loads across opaque calls. Both are trailing
   tokens on the same `load`.
3. **Outer peel, knob-gated, default off.** `BLADE_LLVM_RAGGED_PEEL=N` peels
   the row loop when `rows <= N` **and** offsets are known — one driver over an
   unchanged body closure, mirroring `brickKnob` (`:1474-1482`) and the
   last-tile peel in `emitSimplex2` (`:1531-1546`). When it fires, `%g` becomes
   literal → GEP constant → load folds → literal trip count, i.e. it converges
   on `emitCountedLoop`'s existing form with **no separate emitter**. It is an
   A/B instrument, not a design commitment. **Measured ceiling: single digits.**
   At `nrows = 4` peeling reaches the hand-unrolled optimum exactly; by 64 the
   unrolled form is 10x the instructions for no gain (§2). Default 0; do not
   raise it above ~8 without a measurement saying so.

For the dynamic table, also emit
`call void @llvm.assume(i1 true) [ "align"(ptr %off, i64 64), "dereferenceable"(ptr %off, i64 %bytes) ]`
after construction — a `constant` global carries dereferenceability in its
type, a `blade_alloc_cells` return does not.

---

## 6. A correctness hole to close on the way

**A closed `RaggedIdx<lens>` whose lens array disagrees with its literal
compiles clean and silently uses the literal's shape.** Proven by running, three
independent ways: a runtime-computed lens (`method_for … |> compute`), a
`static function` result, and `extents(gk)` — each deliberately given a value
differing from the literal's structural shape, each typechecking `OK` and each
producing output following the *literal*. Inspecting `blade emit` confirms why:
the construction is always

```cpp
static constexpr const size_t r_lens[3] = {3, 2, 1};
static constexpr const size_t r_offsets[4] = {0, 3, 5, 6};
```

baked from the literal's nested-list structure, and the `lens` binding is never
referenced. Even a plain *literal* mismatched lens is accepted silently. The
annotation is inert (C2); no check exists in `TypeCheckInfer`,
`TypeCheckValidate`, `Unify`, or `CodeGenLoopNest` — there is no ragged
analogue of `checkCompactArrayLit`. The DepIdx path *does* cross-check its
formula against the literal (`CodeGenLoopNest.fs:3737-3748`); the ragged path
checks only that leaf count = sum of literal row lengths (`:3787`). Corpus
tests `019`/`086` write lens arrays that happen to match, so divergence was
untested.

This is a user-visible defect today, independent of the LLVM lane, and is
tracked separately so it need not wait for this plan. The fix below subsumes it
if it lands here first.

(Related grammar limit, also proven: a DepIdx formula cannot reference an array
via call syntax — `DepIdx<Idx<3>, lambda(i) -> Idx<3 - base(i)>>` is a **parse**
error, BL1001, so the "runtime DepIdx formula" route is foreclosed before
typecheck. Consistent with DepIdx being the static tool by design.)

**The fix is the same change that enables the dynamic branch, and the repo
already has the idiom.** SparseIdx solves precisely this — one surface type,
payload either compile-time or runtime — with

```fsharp
type SparseKeysSource = SkStatic of entries: int64 list list | SkRuntime of keys: IRExpr
```

split at lowering by *attempting* static evaluation (`resolveSparseKeysSource`,
`src/TypeLower.fs:916-949`); the static branch **bakes and validates** (uniform
arity, non-negative, no duplicates) and the runtime branch takes a named
rank-1 array. `halo<...>` offsets fold through the same path
(`src/TypeLower.fs:1407-1425`), and an int array literal comes back as
`SVTuple` of `SVInt` — exactly the shape a lens list needs.

So: give `IRRaggedLookup` an analogous `RlStatic of int64 list | RlRuntime of
IRExpr`, resolved by a `resolveRaggedLensSource` mirroring
`resolveSparseKeysSource`, whose static branch validates lens length against
the outer extent and lens sum against the literal's leaf count. That maps
directly onto `RaggedKnown` (§4) and closes the hole as a side effect rather
than as separate work.

---

## 7. Phases

Each is independently shippable; the gate is named skips flipping to passes in
the differential histogram (`tests/LlvmTests.fs:219-247`), judged on the TOTAL
line and the absence of a `Failed tests:` section — **never** the exit code.

### Phase 0 — housekeeping (minutes)
- Delete the stale repo-root `blade_llvm_shim.c` (untracked litter, 205 vs 209
  lines; lacks the `blade_free` null guard at `src/cpp/blade_llvm_shim.c:135-142`).
- Fix `docs/plans/plan-llvm-backend.md:39-42`, which still claims `IRForRange`
  has no arm (it has five; `recursive-arrays` is in the sweep).

### Phase 1 — the ragged substrate, dynamic form
`GRagged`/`GDynDense` (§4) with `RkUnknown` throughout; the accessor API (D3)
enforced from the first commit; `blade_ragged_offsets` shim entry with
construction-time validation so `inbounds` on the pool GEP stays honest.
Consuming peel, row-local `reduce`, `extents(row)`, row sub-view, rank-2 nested
print with per-row bounds, shape-preserving elementwise map, frees via existing
scopes. **No constant-global path yet** — literals go through the dynamic form,
which is correct but not yet optimal.

Gate: ragged files in `index-types` (~22) plus `memfree/017,020` flip to passes.

### Phase 2 — the static decoration
`RlStatic`/`RlRuntime` lowering (§6, closing the correctness hole),
`RkKnown` → constant global + `!range`, and the knob-gated outer peel. This is
a pure subtraction from Phase 1.

Gate: same tests still pass, byte-diff the `.ll` to confirm the decoration is
additive; A/B the knob rather than assuming it pays.

### Phase 3 — padded CSR (D6)
Row starts 64-byte aligned. **Must land together with its guard:** `AVirtFlat`
/ `readFlat` (`:586`, `:1245-1249`) is cell-congruent and stays valid for a
flat *map* over padded rows (pad cells are calloc'd zeros nobody reads), but is
**wrong for a flat fold** where 0 is not the kernel's identity (`(*)`, `max`,
`min`). `hasFlatRead` (`:1252-1255`) must return `false` for padded-ragged in
fold position, forcing the peel. Shipping padding without that guard is a
silent wrong-answer bug.

### Phase 4 — group_by, static key regimes (`Idx<N>`, int `EnumIdx`)
Wedge first: `extents(gk)` + `group_bucket(gk)` (static `ngroups` ⇒ static
sizes throughout). Then the CSR build in pure `.ll` (count with negative-key
drop, exclusive prefix sum, scatter; `switch` for EnumIdx buckets) and
group-contiguous materialization. `Ctx` gains `GroupKeys : Dictionary<IRId,
{NGroups; Offsets; Perm; NSrc}>` mirroring the g++ name-suffixed ABI
(`CodeGenBinding.fs:336-348`) so the differential compares values, not layouts.
The gk binding is `VOpaque`, prints nothing, and `copyArr`'s copy-on-alias must
not touch it. **`__nsrc` stays a gk field, not part of the shared ragged
descriptor** — it exists because negative-key drops under-fill perm, which has
no analogue for other ragged producers.

BL3017 (`src/Unify.fs:238-249`) guarantees a gk never escapes its binding name,
so no lifetime or aliasing story is needed.

Gate: `blade test llvm sql-group-by` (standalone, 49 files); grouped files
already in the default sweep (36 across `loops`/`index-types`) flip.

### Phase 5 — dynamic discovery, multi-key, string keys
Two shim entries per D7 (`_discover` then `_build` — a single call cannot work,
since offsets/perm must be sized from `ngroups`). Multi-key = tuple-keyed
variant. String `EnumIdx` keys (`sql-group-by/005,011`) = emitted compare chain
against constant globals; dynamic string keys stay refused.

### Phase 6 — measure
Grouped and ragged workloads in `llvm-bench`, A/B vs g++, non-power-of-two
extents, interleaved runs, medians. Also A/B the three decorations
independently (`!range`, padding, peel knob) — the reasoning in §2 and §5 is
LLVM-behavior inference, not measurement, and padding in particular is
predicted to be worth more than literal trip counts.

---

## 8. The experiment behind §2 — done, plus one caveat it raises

Three hand-written `.ll` variants of the Phase-1 peel shape (constant-global
offsets table / opaque parameter table / hand-unrolled literal), at `nrows` =
4, 64, 512, with row lengths cycling `3,7,2,5,11,1,4,…` so the offsets are not
an arithmetic progression a strength-reducer could recover. Compiled with
clang 22.1.8, `-O3 -march=native -S`. Results in §2.

Conclusions carried into the plan: the peel knob (D5 decoration 3) has a
useful range of **single-digit row counts only** — set `BLADE_LLVM_RAGGED_PEEL`
default 0 and expect the useful ceiling to be ~8, not ~64; and emit the
`constant` global whenever offsets are known, for the aliasing/CSE win alone,
independent of unrolling.

**The caveat, which is a confound in the experiment and a lead for Phase 6.**
No variant vectorized at any size — LoopVectorize declined all of them, leaving
a scalar unroll-by-8 `vaddsd` chain with an `andl $7` prologue. That is almost
certainly *not* a ragged-shape result: the probe's inner loops are plain `fadd`
reductions with **no fast-math flags**, and reassociating a float reduction is
illegal without them, so SIMD was forbidden by FP semantics before trip counts
ever mattered. The real lane emits `reassoc nsz` inside `withFoldFmf`
(`EmitLlvm.fs:770-805`) exactly when a fold is licensed — and prior measurement
on this branch already found the licence, not the backend, is the SIMD switch
(bricks 1.05x → 1.70x).

Two consequences. First, this experiment **under-measures** the vectorization
question and should not be cited on it. Second, the padded-CSR alignment
argument (D6) only becomes live *once the licence is present* — with no
`reassoc` there is no vector load to misalign. So Phase 6 must A/B padding
**with a licensed fold**, or it will measure nothing. Re-running these three
variants with `reassoc nsz` on the accumulate is the cheap follow-up, and it
also tells us whether a short-row inner loop is worth vectorizing at all
versus vectorizing *across* rows (segmented reduction), which is the
structurally honest alternative and is orthogonal to static-vs-dynamic.

---

## 9. Explicitly out of scope

- **Providers** (netcdf/zarr/csv). `checkModuleScope`'s refusal
  (`EmitLlvm.fs:3504-3505`) stays intact for the whole arc. Note for context:
  provider shapes are read **at compile time** and baked as literals
  (`NetcdfProvider.fs:227-237`), `NC_UNLIMITED` is never queried, VLEN hits a
  raw `failwithf` (`:224`), and the `.stream` fiber path requires literal
  extents (`:650`). A streaming ragged read is a real future, not a current
  capability — the point of D1/D3/D7 is that it would add *provider* code and
  no shape-layer redesign.
- **A chunk/block-list data layout.** Blade's streaming model materializes
  nothing (`StreamingIONotes.md:9-10`); blocking is a loop-order property
  (`ZarrVirtualArraysSpec.md:58-72`), not a storage one. Keep the ragged
  descriptor's base pointer a *field* rather than an ambient register, and a
  block layout stays expressible behind the same two accessors — that is the
  whole insurance premium.
- **A staging-buffer gather path** — D1's contiguity makes it a no-op.
- **Ragged array returns** from functions: `emitFunctionBody`'s single-`APool`
  return arm (`:3431-3458`) keeps refusing. BL3017 means group_by never needs
  it; a `{ptr,ptr}`/out-param design is a separate decision.
- **Runtime DepIdx extent formulas** — the g++ lane refuses them
  (`CodeGenLoopNest.fs:3732`); matching keeps the lanes differentially
  comparable, and DepIdx is the statically-known case by design.
- **SparseIdx, CompoundIdx/mask-compound, unique/intersect/union**; **`lens` as
  a second stored table** (D2); **elementwise map over grouped results**
  (rejected in every lane, `sql.md:391`); **bricking/OMP on ragged**
  (`RowOpBytes = 0`); **LTO** (D5).

---

## 10. Traps recorded during investigation

- `mustprogress`/`willreturn` (`grpFnTerminating :192-200`) is justified today
  as "loops are all statically counted (runtime extents are refused)". **Keep
  the attributes, reword the justification** to the real invariant:
  `emitCountedLoopTo` (`:1093-1114`) splices the bound into the `icmp` after
  evaluating it in the pre-header and never reloads it, so the bound is fixed
  for the loop's duration and the loop terminates. A negative bound gives zero
  iterations (`:1819-1820`). Note the current wording is *already* stale —
  `emitForRange` (`:1827`) and the brick loop (`:1502`) pass registers today.
- `nofree` will drop module-wide once any scope frees a heap offsets table
  (`Ctx.AnyFrees` → `:217`). Expected, not a regression.
- The fn-body grouping regime is recovered from the body's **typed uses** of
  the gk (`CodeGen.fs:109-132`), never assumed dynamic — guessing silently
  changes bucket numbering and empty-group handling.
- Interpreter parity is already exact and cross-referenced both ways
  (`Interp/ArrayOps.fs:1663-1760`, `Interp/Loops.fs:1267-1281`); the LLVM lane
  compares against the g++ lane's bytes, so no interp change is needed.
- Print contract: rank 2 nests, everything else flat; ragged rank-1 rows print
  flat, `Ragged` rank-2 prints nested. The harness flattens a nested actual
  against a 1-D pin (`tests/Expect.fs:695-698`), which is why some ragged pins
  look flat.
