# Round 3 item B -- the relational / whole-region track (research brief)

**Evidence.** Sources read:
- `worktree-bench-fortran` at e64fef3b, with its `bin/Release/net10.0/Blade.exe`;
- `magical-cannon-db55bf`'s wave-3 notes, RESULTS section 16;
- the external planning set: `region-relational-examples.md`, `whole-region-blueprints.md`, `region-compiler-review.md`, `deferred-region-planning.md`, `research-relational-sources.md` and `optimization-architecture.md`.

Compiler use was five invocations, one at a time, from the private cwd `scratchpad/round3/research/B-work/`: `emit p1_semijoin_group.blade` (the R2 sketch), `emit p2_equijoin_inline.blade`, `emit p2b_equijoin_inline_forced.blade`, `plan p1`, and `BLADE_LLVM=1 emit p1`. No build, test, run or g++. The outputs sit beside the probes: `p1.cpp.txt`, `p2.cpp.txt`, `p2b.cpp.txt`, `p1.plan.txt` and `p1.llvm.err.txt`.

**Scope.** The plan's three transformations, plus one more:

| | transformation | note |
|---|---|---|
| T1 | membership hoisting | |
| T2 | hash equijoin | |
| T3 | aggregate-only grouping | |
| T4 | single-pass, demand-sized, shared selection structures | relational rank 3, whole-region rank 5; the lever on the remaining maskagg loss |

Whole-region rank 2 (sibling folds) is not re-proposed: it landed as W1-D (`shareSiblingFoldsModule`, Optimize.fs:3906).

All four are local side-table plans in the round-2 pattern:
- a planner beside `planCompactionFusion`, read by both emitters;
- untouched IR, so the interpreter runs the as-written tree;
- a fail-safe demand census reading the print selection, as `computeExtentsOnlyGroupBys` does (CodeGenExprSupport.fs:1004-1009).

None needs the region review's Stage-0 provenance.

## 1. Hook verification

| plan claim (source) | current location (e64fef3b) | status / evidence |
|---|---|---|
| `contains` in a mask scans linearly per row (relational examples; review `CodeGenExpr.fs:557-573`) | `CodeGenExpr.fs:572-587`, the IRContains IIFE | **holds**: `bool __lambda_25(int64_t c, Array<int64_t,1>& eligible) { return [&]() { for (size_t __ci = 0; __ci < 3; __ci++) ...` (p1.cpp.txt:181-183), called per row |
| set-hoist "attempted, no-op, removed" (`Lowering.fs:2912`, `sql.md:245`) | `Lowering.fs:3089-3090`; `sql.md:244-248`; open item `:809` | **holds, moved. NEW cause:** the mask predicate is called BY NAME via `genCallableWrapper` ("a hoisted set would be unqueried", `CodeGenExpr.fs:2121-2124`). The reference-keyed `SubstMap` scaffolding remains, "NOT YET WIRED UP" (`CodeGenState.fs:1713-1735`, `CodeGenLoopNest.fs:30-42`) |
| sql-semijoins/001's `IRSemijoin` comments are stale | 001 lines 3-14; no `IRSemijoin` in `src/` | holds. NEW: 004's "LOCAL set-hoist ... fuse now" is stale too |
| `IRContains` `IR.fs:164` | `IR.fs:164` (IRMask :160, IRUnique :163, IRGroupBy/IRGroupKeys :187-188, IRJoin :248) | holds |
| LLVM-lane membership | no IRContains arm in `EmitLlvm.fs` | **NEW:** "llvm lane refused: the IR node IRContains in value position -- no arm in the llvm lane; falling back to C++" (p1.llvm.err.txt). Every `contains` program is C++-only |
| grouping = discovery + counts + offsets + perm (`CodeGenBinding.fs:399-500`) | `genGroupKeysBinding` `:618`; dynamic arm `:676-719` | holds, moved. p1 shows THREE key passes, ~3 hash operations per row (find, `[]`, `[]`) and a scattered perm write |
| one contiguous grouped pool (`:2463`); sizes-only gather elision (`CodeGenExprSupport.fs:861`, `:2430`) | `genGroupByBinding` `:2622` (pool `:2810-2818`); `computeExtentsOnlyGroupBys` `CodeGenExprSupport.fs:940`; `elideGather` `CodeGenBinding.fs:2781` | holds; the census model for T3/T4b |
| join traversal (`:4543`); grouped/compound refused (`:4583`); one-level sharing (`:4620-4652`); no thread-level join | `genReduceJoinCore` `:5186`, refusal `:5226`, one-level `:5285` | holds, moved. **CHANGED:** W2-E landed a thread-level join (G7, `BLADE_JOIN_THREADS`), dense legs only |
| LLVM grouping (`EmitLlvm.fs:3918/4356`) | `emitGroupKeysBinding` `:3757`; IRGroupBy `:4721` | **changed detail:** dynamic discovery is a LINEAR scan, O(n*G) (`:3790-3797`); multi-key and string keys are refused |
| `materializeMaskForm` (`:1996`); `genCompoundInitBinding` (`:1908`) | `CodeGenExpr.fs:2050/2060`; `CodeGenBinding.fs:2133`; `genFusedCompaction` `:2313` | holds |
| maskagg compaction fusion (brief-B B4) | `planCompactionFusion` `Optimize.fs:2722` (header `:2613-2653`, table `:2694`, `scheduleMayWriteIn` `:2725`); call `Lowering.fs:3083`; gate `:104`, `optimizerGates` `:123` | **landed** (W1-C): count in the mask pass, one fill+gather pass, no `y*y` pool, repro chain per 256 cells. Positions are ALWAYS built (B4d deferred, `:2652`), `keep` stays stored, and x is still read TWICE |
| `compound_index_t<1>` (`index_types.h:354`) | `:356-357`; counting ctor `:387`; adopting ctor `(n, m, pos)` `:408`; hashed rank-k `:253` | holds |
| top-level-only compound (`Lowering.fs:857`); name-keyed gk (`sql.md:270`); printing follows forcing (`CodeGen.fs:2922`) | `Lowering.fs:893` (BL6002); `sql.md:270-307` (BL3017); `forcedDeferredIdsCell` `CodeGen.fs:2978` | holds; the censuses read `printSelection()` |
| effects / write predicate; optimizer seam (`Lowering.fs:2870-2911`) | `Effects.fs:79/85`; `scheduleMayWriteIn` `Optimize.fs:1343`, `callbackTable` `:1190`; `optimizeModule` `Lowering.fs:3037`, IRLift `:3041`, CSE `:3073`, hot-row `:3078`, compaction `:3083` | holds; the planners go after `:3083` |
| AD group-linear peel subset | `GradFusion.fs:604` (`PKSum/PKMean/PKCount`, `GroupRegime`; AST level) | holds; T3 reuses the vocabulary at IR level |

**Pre-existing defects found by the probes** (register under the next free G numbers; G50 exists on vigilant-hellman):

- **D-B1, compound over an unforced inline map.**
  - Symptom: `compound(method_for(a, b) <@> f, mask(..))` emits `// matched__dense = <deferred computation>`, then reads `pool_base(matched__dense.data)`, which is never declared (p2.cpp.txt).
  - Cause: the computed-dense arm (`CodeGenBinding.fs:2161-2170`) uses `genBinding`, which keeps a bare combinator deferred. With `|> compute` in place, the operand materializes (p2b.cpp.txt).
  - Prediction: BL9002 (not compiled; there is no g++).
  - Fix: `forceDeferredArrayInput`, as the mask-source arm does (`:2194`). This is T2's P0.
- **D-B2, `unique` of a rank-1 compound.**
  - Symptom: `unique(selected_customer)` emits `.extents[0]` and `[__ui]` (p1.cpp.txt, `labels`).
  - Cause: `Compound` has neither member (`nested_array_types.hpp:142-170`).
  - Prediction: BL9002. There is no corpus coverage.
  - Impact: **the planning sketches R1/R2 (`labels = unique(keys)`) passed only `check` and would hit this.** If the interpreter accepts them, the twins diverge.

## 2. The transformations, precisely

Every item is an EMISSION schedule over a side table:
- **Independent check.** `test interp` therefore compares the as-written IR with the new physical form, which an IR rewrite cannot give.
- **Gate comparison.** `opt-diff` compares gate off with gate on, under its pinned `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`.
- **Placement.** Emitters publish placement through the `CountCpp` / `EmittedCpp` handshake (`Optimize.fs:2667-2689`). A lane that cannot place a plan emits today's form, with identical values.

### T1 membership hoisting (`planMembershipHoist`, rule `membership-hoist v1`)

**Shape.** `IRContains (IRVar b, x)` in the body of a callable C evaluated once per cell. C is either a let-bound `mask(src, C)` predicate or a map / reduce / `<&!>` kernel.
- `b` is a materialized rank-1 dense array or rank-1 compound, either a module binding or a capture of C.
- Its element is an integer class: Int64, Int32, `Nat<I>`, or integer EnumIdx.
- C has exactly one reference, as lifted lambdas do.

Antijoins and conjunctions fall out, since only the node is substituted. `contains(B,x) && f(x)` keeps its short-circuit and `f`'s failures.

**Physical.** Before the host binding, emit `blade_member_i64 __mt = blade_member_build_i64(b.data, M)`. A deterministic run-time rule picks the representation, and the choice is recorded:

| representation | chosen when |
|---|---|
| LINEAR | M <= 16 (`member_lin`), so tiny sets never lose |
| BITMAP | span max - min + 1 <= max(64M, 4096) and <= 2^26 bits (`member_span`) |
| HASH | otherwise: open addressing, power-of-two capacity >= 2M, Fibonacci hash, linear probing; occupancy bits, so no key is a sentinel |

Each probe becomes `blade_member_test_i64(&__mt, x)`. Build-side duplicates collapse (R2's `[3,3,1]` trap). Probe-side duplicates and order are kept, because the mask is unchanged.

**Legality.** The output is one Bool per cell, identical to integer `==`, with no FP involved: strict and licensed agree. Float keys are v1.1. They need canonical -0.0 and a NaN that is never inserted or matched; libstdc++'s `std::hash<double>` already equates +-0, but the LLVM core must do it explicitly.

**Refusals.**
- `b` is assigned anywhere, or is a `mut` parameter.
- `b` is a deferred map, or an IRLift per-call let inside C (v1.1).
- The element is float, string, or a tuple.
- C has several sites, or is a CUDA / MPI kernel.

**Write admission (constraint 4): `membershipAdmits`.**
- `not (assignedAnywhere b)`.
- `scheduleMayWriteIn programFuncs modul` on C's body. A kernel writing b through a view changes membership mid-loop: the linear scan sees the change, the table does not.
- The host value passes W+F (`cseFactsOf .MayWrite || writes`).
- No intervening bindings exist, because the build sits immediately before the host.
- A `where omp` host additionally tests every `callbackTable` candidate of C.

**Lanes.**
- C++: a trailing `const blade_member_i64&` parameter on C's function, plus the argument at its one `genCallableWrapper` site.
- LLVM: a new IRContains arm. The linear form ends the whole-program fallback; the hoisted form is a shim call, since `applyKernel` inlines kernels.
- Shared C core: `src/cpp/blade_member.h`.
- Interpreter: unchanged.

### T2 hash equijoin (`planEquiJoin`, rule `hash-join v1`)

**Source shape.** The checked R4 sketch names `equal_keys`, `keep` and `products`, all `|> compute`. The constraint-1-clean spelling keeps the pair pools as compiler temporaries:

```blade sketch
let matched = compound((method_for(left_amount, right_amount) <@> lambda(l, r) -> l * r) |> compute,
                       mask((method_for(left_key, right_key) <@> lambda(l, r) -> l == r) |> compute, lambda(e) -> e))
let match_count = extents(matched)       // the cardinality
let total = reduce(matched, (+), 0)      // left fold in lex (left-major, right-minor) order
```

**Cost today** (p2b.cpp.txt; C++ only, because the LLVM lane refuses rank-2 compounds, `EmitLlvm.fs:7265`). Six passes run over all N x M pairs:

| pass | bytes per pair |
|---|---|
| `==` Bool pool | 1 W |
| identity mask through a wrapper | 1 R + 1 W |
| `vector<bool>` copy | 1 R + 1/8 W |
| `compound_index_t<2>::hash()` enumeration | 1/8 R |
| Int64 products pool | 8 W |
| scatter | 8 + 1/8 R |

The index build also performs J `unordered_map` inserts plus J 16-byte tuple pushes, and the fold over J uses 8 lanes. **About 19.4 B per pair + ~70 B per match.**

**Recognized** at `CompoundInits[J] = (dense, mask)`:
- `dense = IRCompute? (IRApplyCombinator {Arrays = [a1; b1]; not IsCoIteration; pk})`, where pk is a pure total scalar of (l, r) (the `totalScalar` whitelist, `Optimize.fs:2759`).
- `mask = IRMask (E, lambda(e) -> e)`, where E is the outer product over `[a0; b0]` with kernel `l == r` (either order).
- a0 and a1 share the left nominal axis; b0 and b1 share the right one (index identity, not extent).
- The keys are integers; all four arrays are stored and unassigned.
- **Demand census on J.** Every use is `extents(J)` or `reduce(J, k[, init])`, with k ANY pure total callable (order is preserved). J is not printed, keyed, `present`-queried, partially indexed or passed on. Each of those needs the hashed index and its NM-bit mask, so they are v1.1.

**Physical.**
1. Build a CSR over b0. A positional key type (`Array<KIdx like R>`) gets a counting sort; otherwise the T1 core serves as the key-to-bucket map. **There is no negative-key drop:** equality has none, unlike `group_keys` (sql.md:333). A positional left key outside [0, K) is "no match", not BL8006.
2. Probe i ascending, and walk each bucket in ascending j, writing `pk(a1[i], b1[j])` into `compact[q++]`.
3. J stays STORED as the named relation, built with a cardinality-only `compound_index_t<2>` constructor. Consumers are unchanged.

**Legality.**
- The visiting order (i asc, j asc) IS the compound's lex storage order (BladeLex), so J's cells and their order are bitwise the same. The unchanged consumer folds see the same chain under strict and licensed alike.
- The only skipped work is `==` and pk on non-matching pairs, and both are pure and total.
- Bags survive: the R4 fixture gives 4 matches and total 120. A set-based probe would give 2 and fail the pin.

**Refusals.** Non-whitelisted pk or `==` (integer `/ % ^`, calls, captures); float keys; more than two axes; a non-identity predicate; a census miss; a conjunctive equality kernel (v1.1).

**Write admission: `equiJoinAdmits`.**
- W+F on the producer kernels (pk, `==`, the identity predicate) and on J's whole value.
- `assignedAnywhere` false for all four sources.
- No consumer moves (the folds read the stored J), so the outer kernel and the seed are untouched.
- A v1.1 fold-into-probe would need B4a's snapshot proof: W+F from J through the fold host.

**Lanes.**
- C++: v1, after the D-B1 fix.
- LLVM: v1.1, a `.ll` CSR build in the style of `emitGroupKeysBinding` plus an inline probe. No rank-2 compound support is needed while J stays index-free.
- Interpreter: unchanged.

### T3 aggregate-only grouping (`planGroupAggregate`, rule `group-aggregate v1`)

**Shape.** `let gk = group_keys(k)` has one rank-1 integer key, dense or a rank-1 compound, in any of three regimes: positional `Idx<N>`, integer EnumIdx, or dynamic.
- gk's only uses are `group_by(v_i, gk)` and `extents(gk)`; `group_bucket` is v2.
- Each `g_i` is unprinted (`printSelection`), used nowhere else, and consumed only by ragged peels (`tryRaggedPeel` / `tryGroupedZipPeel`, `CodeGenCuda.fs:2516/2811`).
- Each peel kernel is in the IR twin of GradFusion's `PeelKernel` subset, widened: `reduce(r, k[, init])` with k ANY pure total two-parameter callable, `prodsum(r, r)`, `prodsum(ra, rb)` over same-gk rows, and `extents(r)`. Means and tuple joins are v1.1.
- The peel outputs stay stored as named arrays.

**Physical: one pass in source-row order.** For each row i:
1. Read `__k = k[i]`, applying the negative-key drop and the BL8006 positional guard exactly as today, at gk's span.
2. Find g. Positional: g = k. EnumIdx: the static lookup. Dynamic: ONE find-or-insert in the C core, numbering groups by first occurrence (this replaces discovery, counts and perm).
3. On a group's first row, initialize each state: from `init` when seeded, else by first element, or with the exact identity (-0.0 for float `+`, per the W1-D ruling).
4. `acc_j[g] = k_j(acc_j[g], v_j[i])`; `cnt[g]++`.

After the pass, `offsets` is derived from `cnt` (G-sized) for `extents(gk)`. There is no perm and no pool: the rows stay null, as in the gather elision. Each peel binding becomes `out[g] = acc_j[g]`. An unseeded positional fold also raises BL8003 for the FIRST empty g, in group order, at the peel's declaration.

**Legality (grouping order).**
- Group identity and order are unchanged. Positional and EnumIdx are numbered by key value; dynamic uses the same first-occurrence counter.
- Members of each group are visited in ascending source row, which is exactly gk's stable perm order. Every chain therefore keeps its operands, order, seed and left association. **Strict-legal: bitwise under `BLADE_FP_REPRO=1`, both lanes.** Interleaving across groups only reorders independent chains.
- Licensed: today's peel runs a per-group K-lane tree (8 integer lanes in p1, likewise for float). The one-pass form runs the ascending serial chain instead. That is a legal association under the per-fold licence. Bits may move (as with B4's 2 ulp), within the harness's 1e-9 rule; integer folds are exact either way.
- A peel's `where omp` map licence is never transferred to the fold, and licences are never unioned.
- Failure order is unchanged. The pass raises only BL8006, at gk's site, where today's count pass raises it first. Accumulation is total, so an intervening failing binding still fails first. BL8003 stays at the peel.

**Refusals (the CSR and gather stay).**
- A row-reading consumer such as `r(0)`.
- Co-iteration with a non-gk operand.
- A printed or escaping `g_i`.
- Multi-key or string keys.
- A kernel outside the subset, or one that fails, writes or calls.
- A `where omp` peel (`sql-group-by/033`).
- `.stream` sources (left to `segment-streaming`).

**Write admission: `groupAggAdmits`.** Accumulation MOVES from the peels up to gk, so all five parties are checked:

| party | check |
|---|---|
| producers k, v_i | `assignedAnywhere` false |
| each peel kernel | W+R |
| each outer fold kernel k_j | W+F |
| each seed | W+F, and a pure scalar: it is now evaluated at gk time |
| every binding from gk to the last peel | W+F |

This is the G41 compaction-fusion row, plus the seed and the outer kernel.

**Lanes.**
- C++: the grouping and peel emitters.
- LLVM: positional and EnumIdx in v1. The dynamic regime uses the C-core hash, which also retires the O(n*G) discovery (`EmitLlvm.fs:3790-3797`) for every dynamic grouping.
- Interpreter: unchanged.

### T4 selection structures: single pass, demand-sized, shared (`compaction-fusion v2`)

T4 extends B4's planner, using the same table and handshake.

**T4a, single pass.** When `keep = mask(x, pred)` feeds a planned compound, the MASK pass also gathers: `b = pred(x[i]); keep[i] = b; y[q] = x[i]; pos[q] = i; q += b`.
- y and pos are sized n+1, like the expert's `y(n)`. Only m+1 cells are ever written; untouched pages are never committed.
- This removes the second full read of x and the re-read of keep.
- `keep` stays stored (constraint 1; B4's ruling).
- Under repro, B4c's in-pass chain moves into this pass, unchanged (completed cells, index < q).
- If a non-static n x elem exceeds `sel_cap` (256 MB), the B4 two-pass form remains.

**T4b, positions elided (B4d).** A fail-safe census, `computeCompoundIndexDemand`, is modelled on `computeExtentsOnlyGroupBys`.
- The position table is skipped when every use reads only `y.data` or the cardinality: folds and the planned fold map, `extents(y)`, `contains(y, .)`, `group_keys(y)` and `group_by(y, .)` (compact order), `sort(y)`, a zip with a compound over the same mask, and printing.
- The table is still built for keyed `y(k)`, `range<CompoundIdx<keep>>`, `present`, projections, arguments and tuples.
- The adopting constructor (`index_types.h:408`) takes `pos = nullptr`. A keyed read of a null table is an internal error, made unreachable by the census.

**T4c, one selection for companions** `y_j = compound(c_j, keep)`, with each c_j over x's axis.
- Today each y_j builds its own `compound_index_t<1>`, with its own count and fill passes (p1: `selected_customer_idx`, `selected_amount_idx`).
- B4 declines every companion: "the mask's source is not the compound's dense operand" (p1.plan.txt).
- Under T4c, ONE index is built in the mask pass, owned by the plan, with a new `deallocate_compound_shared` so no compound frees it.
- The J gathers become one pass over the positions, or fuse into T4a's pass when J <= 4.

**Legality.** The compaction stays stable, ascending and serial: identical values, cardinality and (when built) positions. The folds are untouched (B4a / B4c). Byte-identical in both regimes.

**Refusals.** Rank >= 2 or trailing dims; an impure predicate; `keep` assigned, or read between the mask and the last companion by anything but a planned compound; a companion written in between.

**Write admission: `selectionAdmits`** (the compaction-fusion row). Producers x and every c_j have `assignedAnywhere` false. The consumer predicate satisfies `totalScalar`. Everything from the mask through the last compound passes W+F. The outer kernel and seed are as in B4.

**Lanes.**
- C++: `materializeMaskFormCounted` (counted arm `CodeGenExpr.fs:2138-2147`) and `genFusedCompaction`.
- LLVM: the IRMask arm (`EmitLlvm.fs:4874`, handshake `:3992-3996`) and `emitCompoundInit`. **LLVM is maskagg's reference arm, so T4a/b land there first.**
- Interpreter: unchanged.

## 3. Target cells and the work that disappears

Logical incremental bytes, at n = 1,000,003 unless stated.

| cell | current ratio (gf / ifx) | item | mechanism |
|---|---|---|---|
| maskagg serial licensed | 1.062 / **0.788** (RESULTS 15.3 NEW) | T4a+b | Today 30 MB in 3 sweeps: mask pass (x 8.0 R, keep 1.0 W), fused pass (x 8.0 R, keep 1.0 R, y 4.0 W, pos 4.0 W), fold (y 4.0 R). T4a: 21 MB. T4a+b: **17 MB, 2 sweeps**. The expert reads x once (8), stores into y(n) (~4), sums y (4): ~16 MB |
| maskagg serial repro | 1.062 / **0.813** | T4a+b | 26 -> **13 MB, 1 sweep** (B4c's chain is in the pass); the expert is still ~16 MB |
| `semiagg` (NEW), licensed / repro | ~0.003 (estimate) | T1 (+T4c) | M = 1009, about 985 distinct ids in [0, 20011). Each miss (95% of rows) scans 1009 entries: **~0.98e9 compares, ~8 GB of L1 re-reads**. After: one 2.5 KB bitmap and n bit tests, with `compound(amt, keep)` fused (T4c) |
| `groupagg` (NEW), G = 1009 positional | new | T3 | Today: 2 key passes (16 R), scattered perm (8 W), gather (perm 8 R, x 8 random R, pool 8 W), 2 peels (16 R) = **64 MB, 6 sweeps, 16 MB scratch**. After: **16 MB, 1 sweep, ~24 KB of state**. Dynamic keys: 3 -> 1 hash operations per row |
| `hashjoin` (NEW): N 100,003, M 20,011, K 10,007, J ~2.0e5 | **infeasible today**: 19.4 B x 2.0e9 pairs ~ 39 GB, against a 64 MB cap | T2 | Build over M (keys 160 KB, offsets 80 KB, perm 160 KB), probe N (1.6 MB), J evaluations (b1 read from L2), J stores (1.6 MB): **~4 MB**, O(N+M+J) |
| `filtergroup` (NEW, the R1 shape) | new | T4c + T3 | 3 index builds -> 1 (saves 2 x (n R + n R + 8m W)); 3 gathers -> 1 pass; then T3's single pass |

**Kernel proposals and expert twins** (for item C). Inputs are `|> compute`; outputs are stored; there is no mt8 arm (no new licence); no extent is a power of two.

| kernel | Blade source | gfortran / ifx expert | natural tier |
|---|---|---|---|
| `semiagg` | top level: `keep = mask(cust, lambda(c) -> contains(elig, c))`, `y = compound(amt, keep)`, `checksum = reduce(y, (+)) + Float64(extents(y))` | a `logical(1)` bitmap sized by `maxval(elig)`, maskagg's branchless compaction, then the sum pass | `pack(amt, [(any(elig == cust(k)), k=1,n)])`, O(nM) |
| `groupagg` (+ `_dyn`) | in a function: `gk = group_keys(key)`; peels `reduce(r, (+))` and `prodsum(r, r)`; `c = extents(gk)` | a one-pass direct-index accumulator; `_dyn` adds an open-addressing hash with first-seen numbering | `s(g) = sum(x, mask = key == g)`, O(nG) |
| `hashjoin` | the T2 inline spelling; `match_count`, `total`, and `fp = reduce(matched, lambda(a, b) -> 3 * a + b, 0)` (exact Int64; fp pins the ORDER) | a counting-sort CSR on the right keys over [0, K), then a left probe | nested `do` loops, O(NM) ~2e9 (shrink the size or drop this tier) |
| `filtergroup` | the R1 sketch minus `unique` (D-B2); labels = the positional range | one pass: predicate, then direct-index accumulate | per-group masked intrinsics |

`hashjoin` gets an OLD figure only at a pilot size (N 2003, M 1009, K 401: 2.0e6 pairs, 39 MB). The `semiagg` and `groupagg` OLD arms run at full size.

## 4. Implementation design

**Tables** (Optimize.fs, after `:2722-2950`; AsyncLocal, reset per program like `CompactionTable` `:2694`):

| table | fields |
|---|---|
| `MembershipTable` | `{CallableId; ProbeOrdinals; SourceId; HostId; EmittedCpp/Llvm}` |
| `EquiJoinTable` | `{CompoundId; Left/Right (keyId, payId); PayloadKernel; KeyRegime}` |
| `GroupAggTable` | `{GkId; Regime; States (peelId, kernel, init, kind); ValueIds}` |
| `CompactionPlan` (extended) | gains `SinglePass`, `PositionsNeeded`, `Companions`. `ByMask` becomes one plan per mask with companions; today a second compound overwrites the entry (`:2707`) |

The planners run at `Lowering.fs:3083ff`, after IRLift and CSE, so node ordinals are stable. Each admission predicate logs its five-party line in the gap register's G41-row style.

**Knobs (constraint 5).** All four go in `optimizerGates` (`Optimize.fs:123`), so the suite clears them and opt-diff compares off against on. Each extends the CLAUDE.md per-pass row (`:203`).

| knob | values |
|---|---|
| `BLADE_MEMBER_HOIST` | unset / `1` / `on`; `linear`, `bitmap` or `hash` force a representation for A/B (`bitmap` only where the span bound holds); `0` / `off` gives the IIFE |
| `BLADE_HASH_JOIN`, `BLADE_GROUP_AGG` | unset / `on`, or `0` / `off` |
| `BLADE_COMPACTION_FUSION` | gains `b4`: the exact round-2 form, T4's knob-off mirror |

`BLADE_TILE_TUNE` gains speed-only keys: `member_lin=16`, `member_span=64` and `sel_cap`.

**Plan records.** Each refusal prints `declined -- <reason>`. The applied forms:
- `[membership-hoist v1] keep: applied [build once before keep; integer keys; representation at run time: linear <= 16 | bitmap span <= 64 M | hash; 1 probe site]`
- `[hash-join v1] matched: applied [CSR over right_key; left-major/right-minor = lex order; N x M pools never built; J stored, index cardinality-only]`
- `[group-aggregate v1] gk: applied [one pass; states s, q, c; no perm, no gather; chains in source order]`
- `[compaction-fusion v2] y: applied [single pass (n + 1 buffers); positions elided: no keyed read; index shared with y2, y3]`

**C++ lane.**
- T1: `CodeGenExpr.fs:572` looks up its probe ordinal, otherwise the IIFE. The function emitter adds the table parameter; `genCallableWrapper` forwards it. A new `genMembershipBuilds` is called from `genBinding` (`CodeGenBinding.fs:164`) and frees through `registerShapedAlloc`.
- T2: `genCompoundInitBinding` (`:2133`) consults `EquiJoinTable` before the rank-k path (`:2262ff`). It needs D-B1 (`:2161-2170`) and a cardinality-only `compound_index_t<2>` constructor near `index_types.h:253`.
- T3: `genGroupKeysBinding` (`:618`) emits the pass. `genGroupByBinding` (`:2622`) uses the null-row arm (`:2789-2790`). The peels (`CodeGenCuda.fs:2516/2811`) emit `out[g] = acc[g]` plus the BL8003 check.
- T4: `materializeMaskFormCounted` (`:2138-2147`) does the fill; `genFusedCompaction` (`:2313`) adopts.
- Runtime: a new C99 `static inline` core, `src/cpp/blade_member.h` (member set plus key-to-bucket map), shared with the LLVM shim in the `blade_llvm_tiles.c` pattern.

**LLVM lane.**
- A new IRContains arm: the linear scan plus the `blade_member_*` probe.
- `emitGroupKeysBinding` (`:3757`): the T3 pass, and the shared map for every dynamic grouping.
- The IRMask arm (`:4874`) and `emitCompoundInit` (around `:7265`): T4a, T4b and T4c.
- T2 lands in v1.1. Licensed folds keep their `reassoc` trees.

**Interpreter.** No change. `test interp`, across every discovered corpus directory, serves as the independent oracle. D-B2 may need a twin check.

**Tests.** EXPECT pins; the int / float spelling is checked.

| item | location | cases |
|---|---|---|
| T1 | `sql-semijoins/008-016` | `[3,3,1]` build duplicates; antijoin; short-circuit pin `contains(B, x) && 100 / x > 3` with 0 not in B; empty B; negative keys (no drop); hash and bitmap spans; M <= 16; compound B; contains in a map kernel; assigned-B and kernel-writes-B declines. Plus `tests/OptimizeTests.fs` `decision_membership_hoist_*` and llvm goldens |
| T2 | new discovered directory `tests/corpus/sql-joins/` (in `blade test sql`; Corpus Wiring) | `001` R4 fixture (`match_count = 4`, `total = 120`); `002` order fingerprint `3 * a + b`; `003` negative keys match; `004` / `005` no matches, seeded (= init) and unseeded (`// ABORT: BL8003`); `006` a keyed `J(0, 1)` declines, same values; `007` a writing payload declines; `008` / `009` dynamic and positional keys. Plus `index-types/522` for D-B1 |
| T3 | `sql-group-by/064-076` | one pass over dynamic sum / prodsum / extents; positional empty groups seeded and unseeded (ABORT BL8003); EnumIdx; negative-key drop; within-group fingerprint `reduce(r, lambda(a, b) -> 3 * a + b)`; compound key source (R2 without `unique`); declines for a row-reading second peel, a printed grouped array, a `where omp` peel; `051` and `056` under the gate |
| T4 | `index-types/523-530` | single pass, licensed and repro; companions sharing an index (`y1(k)` and `y2(k)` agree); a positions-census negative; `keep` read in between declines; the `sel_cap` fallback |

Blocks to run: `sql`, `index-types`, `optimize`, `llvm`, `opt-diff`, `interp`, `docs`, then the full suite. Gate on the TOTAL, never on the exit code.

**Docs.**
- `docs/features/sql.md`: section 5 (hoisted build), section 6 (performance status), an aggregate-only paragraph in 7/8, section 2 (single-pass and shared selection), and close open item 1.
- The CLAUDE.md env row.
- Hand-edit the stale comments in sql-semijoins/001 and /004 (comments only; the files are byte-pinned).

## 5. Effort, risks, and the experiment that should make us DROP each item

Every DROP experiment hand-patches the emitted C++ (the `compaction-drop.md` method) and runs 21 interleaved rounds.

| item | effort | main risks | DROP experiment |
|---|---|---|---|
| D-B1, D-B2 | S | -- | none (correctness) |
| T1 | M (core S, C++ M, LLVM arm M) | plumbing an opaque table parameter through lifted lambdas (MEMORY: capture-forwarding seams); multi-site callables; float keys; IRLift per-call lets | `semiagg` at M = 1009 and M = 31: if bitmap and hash differ by < 10% at both, drop the chooser and ship hash plus the linear crossover. T1 itself survives any plausible outcome (OLD is ~1000x off) |
| T2 | L (C++ v1 M, LLVM M) | J demand census; constraint-1 status of the NAMED R4 spelling; skew (J -> NM) | If the coordinator keeps R4's named bindings stored AND rejects the inline spelling, drop v1: nothing NM-sized can disappear, and a join operator becomes a language question. Otherwise, at pilot size, if NEW < 0.8x the expert CSR join, return to design |
| T3 | M (C++), +M (LLVM dynamic hash) | licensed float with few groups: serial chains are latency-bound at G <= 3, versus K lanes; hash cost for dynamic keys | `groupagg` at G in {1, 3, 1009, 100003}, licensed and repro. Drop the licensed-float arm (keep repro and integer) if one-pass loses > 10% at G <= 3. Drop T3 if it is < 1.3x OLD at G = 1009 (expected 2-4x) |
| T4 | S (T4a), M (T4b census), M (T4c ownership) | virtual n+1 buffers (`sel_cap`); shared-index ownership; census completeness | maskagg licensed / repro: B4 vs T4a vs T4a+b, LLVM first. Drop T4a if it saves < 0.4 ms incremental; drop T4c if `filtergroup` gains < 10% |

**Ranked recommendation.**
1. **T4a+b**: it targets the remaining maskagg ifx loss, is small, and adds no new semantics.
2. **T1**: an asymptotic win that also unlocks the LLVM lane for `contains`.
3. **T3**, together with T4c.
4. **T2**: it waits on D-B1 and the constraint-1 ruling.

## 6. Which N01-N10 workloads exercise these (for item C)

- **N10** (grouped nonlinear least squares) is the principal T3 workload: objective, gradient and packed H are several fold states over one scan.
  - Its states are vector / packed (p(p+1)/2 per group), so it needs T3 v2 with tuple or vector states.
  - If samples are contiguous by group, the structural `segments(..)` regime applies instead: no hash, no perm (`genSegmentsBinding`, `CodeGenBinding.fs:2848`).
- **N03** (ragged online attention) uses only the segment grouping; its online max / denominator / numerator state is a T3-v2 tuple fold.
- **N07** shares only the `segments` vocabulary; its mechanism is ghost reuse.
- **N01, N02, N04, N05, N06, N08, N09** use none of the four.

From the other 35:

| workload | what it exercises |
|---|---|
| N19 histogram | the purest positional T3 case |
| N29 FE assembly | T3 with destination ownership |
| N18 masked group normalization | T4c + T3 + a `group_bucket` gather-back |
| N40 grouped online covariance | T3 v2 |
| N26 / N27 sparse merge | T2's merge-join alternative; needs a sortedness certificate, which hashed, given-order `SparseIdx` does not provide |

## 7. File-overlap list against the other round-3 items

| file : region | B (this) | A census / losses | C N01-N10 | D orbit / symmetry |
|---|---|---|---|---|
| `Optimize.fs:123` gates; `Lowering.fs:3075-3090` planner calls | 4 gates, 3 planners | likely | likely | likely. One-line conflicts: serialize the merges |
| `Optimize.fs:2613-2950` compaction planner / table | T4 | **maskagg is A's remaining loss; assign it to ONE item (recommend B, T4a+b)** | -- | -- |
| `CodeGenBinding.fs:2133-2350` compound init / fused compaction | T2, T4 | maskagg | -- | -- |
| `CodeGenBinding.fs:618-753`, `:2622-2838`; `CodeGenCuda.fs:2516-2945` peels | T3 | -- | N10, N03 | -- |
| `CodeGenExpr.fs:572`, `:2060-2160` | T1, T4 | maskagg | -- | -- |
| `EmitLlvm.fs:3757`, `:4721`, `:4874`, `~:7265`; the new IRContains arm | T1, T3, T4 | maskagg (the LLVM ref arm) | N10 | orbit paths elsewhere; disjoint |
| `index_types.h:253-420`; `nested_array_types.hpp:142` | T2, T4 | -- | -- | -- |
| new `blade_member.h` + shim; `CodeGenExprSupport.fs:940` | T1, T3, T4b | -- | N10 dynamic keys (maybe) | -- |
| CLAUDE.md `:203`; `sql.md` | yes | yes | yes | env rows |
| `bench/kernels/{semiagg,groupagg,hashjoin,filtergroup}` | proposals | -- | C owns the catalogue: dedupe `groupagg` against N10 / N19 precursors | -- |

## 8. Open questions for Codex (least-sure first)

1. **T3 failure order.** Accumulation moves to gk's site, BL8006 is raised in the pass, and BL8003 at the peel afterwards. Is the first observable failure, and its span, always unchanged? Attack it with an unseeded positional peel; with a display or a BL8013-failing binding between gk and the peel; and with two planned peels where the later one fails first in the original.
2. **T3 licensed.** Replacing a licensed per-group K-lane tree with the ascending serial chain: is that a legal use of the per-fold licence, given no map licence is spent and bits move as in B4's 2 ulp? Should the G <= 3 latency case get a static-G K-lane rule instead of a decline?
3. **T2 skipped work.** Are `==` and pk on non-matching pairs truly unobservable under `totalScalar`? Is a positional left key outside [0, K) correctly "no match"? Is left-major / right-minor exactly `compound_index_t<2>`'s lex enumeration (`index_types.h:229-232`)?
4. **Constraint 1.** Are user-named, unprinted top-level bindings named outputs? Cases: maskagg's `keep`; R4's `equal_keys` / `products`; the R1/R2 companion compounds. T2 v1 and T4 are designed to need no ruling; v2 depends on one.
5. **T1 admission completeness.** Is "`b` unassigned, plus `scheduleMayWriteIn` on C and the host, plus `callbackTable` for a parallel host" enough when b is reached through a helper called from C, or when b is a compound whose companion is assigned?
6. **D-B1 / D-B2.** Confirm both with g++ in the build round. Does the interpreter accept D-B2's program? If so, the twins diverge and no corpus file covers it.
7. **T4a memory accounting.** Do the n+1 y / pos buffers (untouched pages uncommitted) change the harness's peak-working-set figure or any allocation-inclusive rule?
8. **T1 determinism.** The chooser depends only on (M, min, max), and the booleans are representation-independent. Can anything else leak, for example iteration order if a later `unique` / `intersect` reuses the core?
9. **Oracle independence.** C++ and LLVM share `blade_member.h`; the interpreter is independent. Is the llvm-vs-cpp differential enough, or does the core need its own raw unit block, as `fold-lanes` has?
