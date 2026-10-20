# A -- ordered orbit scatter + G35 parallel complete-row repro folds (research brief)

Master 4136f0b5, Release binary `bin\Release\net10.0\Blade.exe`. Everything below was read
in current source or in code emitted by that binary (scratch: `round2\A-work\`). The
symbolic checks and the raw-bit prototype are scratch artifacts, not repo changes; no timings
were taken.

**Bottom line.**
- **Build G35 first.** It is C++-only, reuses the Path B team and partition, needs no new
  proof, and targets the worst cells (mt8 repro pairkernel 0.370 / 0.359, comoment3
  0.478 / 0.427).
- **Then the ordered scatter, rank 2 before rank 3.** It needs both lanes, because blade-llvm
  is the reference lane for serial repro. Its targets are serial repro pairkernel 0.903 / 0.840
  and comoment3 0.773 / 0.780.
- **Scope is narrower and wider than the plan.** The two forms serve different
  (build, thread) cells. The scatter is threadable only as a wavefront, so it is the serial
  answer and G35 is the mt8 answer.
- **New since the plan:**
  1. The scatter needs no licence of any kind. It works for any pure, total fold kernel and
     any loop-invariant seeds, not only `(+)` with +0.
  2. A G-row-group form makes the scatter vectorizable without touching any chain's order:
     lanes are distinct fold states, never one chain split.
  3. Rank 3 is the rank-2 scatter applied slice by slice, plus one elementwise event stream.
  4. The scatter's first-panic order provably equals the walk's at G = 1.
  5. A C++ prototype is raw-bit equal to the walk on 176/176 cases at `-ffp-contract=off`.
     It is NOT equal under `fast`, and neither is the walk to itself across contexts (see
     section 2.4).

## 1. Hook verification

| Plan claim | Current location | Status | Evidence |
|---|---|---|---|
| Shape / summand recognition `OrbitFold.fs ~:312` | `src/OrbitFold.fs:314 recognise` (`levelOf` :299, `collectReads` :219, `substReads` :230, `pureScalarBody` :239, `compactGroupOf` :209) | holds | `recognise` walks nested `IRReduceCompute` levels over `range<I>`, finds ONE compact read at a permutation of the level vars and substitutes `readVarId` (-770001) |
| Licensed / repro veto `~:423-426` | vetoes spread over :402-426 (`badFold`: builtin `+` :404, `foldReorderLicensed` :406-407; `badSeed` inner seeds +0 :412-417; knob :423-424; `fpReproEnabled` :425-426) | holds, wider than claimed | `elif fpReproEnabled () then decline "BLADE_FP_REPRO=1 keeps the logical walk's serial association"`. The structural checks end at :410, so a strict plan can share :314-400 |
| `CodeGenBinding.genOrbitFoldCpp ~:3562` | `src/CodeGenBinding.fs:3562`. Dispatch is in `genReduceComputeBindingCore` :3712: `recognise` call :3746, declines :3749-3760, taken :3762-3764 | holds | Binding-level only. Threaded branch (G32) :3666-3700 |
| `EmitLlvm.emitOrbitFold ~:6002` | `src/EmitLlvm.fs:6006` (`tryOrbitFold` :5992, called first thing in `emitReduceCompute` :6107-6109) | drifted +4 | `tryOrbitFold` requires `soleSimplexR` and `hasFlatRead` (pool or `AVirtFlat`) |
| G34 offset arithmetic `SimplexBlocksCore.coordTerm` | `src/SimplexBlocksCore.fs:241`. LLVM only: `coordTableGlobal` :2084, `coordTableOffset` :2097, `canonRead` :2147 | holds (LLVM lane) | Emitted comoment3 `.ll`: `@.blade.ctab.0/1 = private unnamed_addr constant [171 x i64]` plus a 3-compare / 6-select network, 2 table loads, 2 adds, 1 pool load per logical cell |
| C++ lane repro read | `CodeGenExpr.renderIndexExpr` :819 (network spelled at :921-947) and `src/cpp/nested_array_utilities.hpp:910 canon_fold<R>` | different from LLVM | The C++ lane does NOT use coordTerm: it uses `canon_fold` (compare-exchange), `canon_left_justify`, then the NESTED row-pointer walk `t[c0][c1][c2]`. The comment at :937-945 says the flat form measured slower (M2) |
| Where the repro walk is emitted (C++) | Non-orbit path of `genReduceComputeBindingCore`: `foldNestLines` :4405, `recordFoldForm ... "serial (repro)"` :4413, then `genLoopNest` | located | Outer level = the binding's nest. Each inner level = a kernel-body fold rendered as its own function (`__lambda_80`, `__lambda_108/112`) |
| Unseeded reduce (first-term init) | `TypeCheckInfer.fs:2823-2848` (`tryInferReduceCompute` seed) | does not arise | A fused deferred reduce ALWAYS gets a seed: `(+)` → `0` / `0.0` / `TExprZero`, `(*)` → `1`, a lambda kernel without init is an error. Emitted: `double __ret1073741824 = 0.0;`. First-term init (`__r = row[0]` + BL8003) exists only for `IRReduce` over a materialized array |
| G35 Path B hook | `CodeGenBinding.fs` `chunkable` :3897-3905, `chunkForm` :3916-3928, `FoldChunk` :3930. `ChunkForm` DU `src/IRStorage.fs:111-116`. Consumer `CodeGenLoopNest.fs`: prologue :1560-1640 (`blade_fold::chunk_lo` :1631), chunked header :1829-1833, fold statement :2132-2140, combine :2189-2194, repro census :1807-1808 | holds | Under repro the plan is built (route selection is knob-independent by design, comment :3867-3877). Its consumer prints `[omp] requested but emitted serial: BLADE_FP_REPRO=1 ...` |
| LLVM-lane form strip | `CliCommands.fs:988-989 llvmLaneDecision` | holds | Rewrites the orbit `threadedSuffix` for the serial LLVM lane. G35 needs the analogous rewrite (LLVM takes the scatter instead) |
| Knob registration | `CliSelfTests.fs:83-94 suiteClearedKnobs` (`BLADE_ORBIT_FOLD` :93); `Optimize.fs:77 optimizerGates` | holds | `BLADE_ORBIT_FOLD` is a codegen knob in `suiteClearedKnobs`, NOT in `optimizerGates` |
| Test homes | Plan fixtures `tests/OptimizeTests.fs:1283-1320`, cases ~:1660-1715; threaded orbit `tests/OmpTests.fs:1567-1623`; raw-bit blocks `tests/LinAlgTests.fs:1569 runSyrkTileTests`, `:1676 runFoldLanesTests`; dispatch `CliSelfTests.fs:3976/3981`; corpus `symmetry/112-115, 123, 124` | holds | Model for new blocks |
| Cell / state counts in the plan | -- | verified | pairkernel pool C(2504,2) = 3,133,756; logical 6,265,009. comoment3 pool C(173,3) = 848,046; logical 5,000,211; pairs C(172,2) = 14,706 |

**Bench checksums carry licences.** Both kernels spell the checksum `reduce(... lambda(j) ->
reduce(... lambda(k) -> t(i,j,k) * t(i,j,k), (+)), (+)), lambda(u, v) where omp -> u + v, 0.0)`
(rank 2: one fewer level). The inner folds are builtin `(+)` (licensed, seed +0.0 by
inference) and the outer is a builtin-`+` lambda with `where omp`, seed `0.0`. In the default
build every level is licensed and the orbit-class fold takes the nest. In repro it declines at
:426 and the logical walk runs.

## 2. The transformations, precisely

### 2.1 Recognized input (shared with the orbit fold)

`IRReduceCompute(IRApplyCombinator{Arrays=[IRRange([ix],None)]}, kernel_L, seed_L)`, nested r
levels, r = 2 or 3. The innermost body is `f(S(v_perm))`, where:
- S is a materialized SymIdx<r, n> pool (C++: `VarNames` hit, not in `DeferredComputations`;
  LLVM: `hasFlatRead`);
- f passes `pureScalarBody`: literals, invariant scalars, `IRBinOp` / `IRUnaryOp` / `IRIf` /
  `IRFma` / `IRLet`, with no calls, other arrays, or level coordinates.

All of this is lines :314-400 today. The strict plan drops four of the five vetoes (builtin
`+`, reorder licence, +0 inner seeds, repro) and adds the five checks below.

1. **Every level strict in this build:** `fpReproEnabled ()`, OR every level's kernel fails
   `foldReorderLicensed`. A mixed nest (licensed inner, `where repro` outer) keeps today's walk
   in v1, because making a licensed inner level strict changes the default build's bits for no
   claimed gain.
2. **Pure, total fold kernels:** each level's callable body is a scalar expression of its two
   params, made of `+ - *` on floats or integers (integers wrap), float `/`, `min` / `max` /
   `abs`, comparisons and `IRIf`. An eta-wrapper around a named function is checked in the
   callee (`foldKernelReproVetoed` already resolves that shape, CodeGenExprSupport.fs:1625).
   Refuse: integer `/` `%`, integer `^`, float→int casts, calls, captured-`mut` writes. Reason:
   the scatter interleaves kernel applications across chains, so an effectful or panicking
   kernel could change observable order.
3. **Seeds are literals or invariant scalar variables** with no level coordinate: evaluated
   once and hoisted. Per-row seeds `seed(i)` are legal at rank 2 but deferred to v2. Rank 3
   additionally needs symmetric seeds to share H.
4. **f is total for the grouped (G > 1) form:** no integer `/` `%`, no float→int cast. The
   G = 1 lex form needs no totality (section 2.2).
5. **Bounds:** symmetric only, real numeric element type, one accumulator type across levels,
   static n ≥ 1. State is n accumulators (rank 2) or C(n+1, 2) (rank 3), capped at 2^20
   doubles. pairkernel uses 2,503 (20 KB); comoment3 uses 14,706 (117,648 B).

### 2.2 Rank-2 ordered scatter (the planning proof, extended)

The source is `fold_out(seed_o, [fold_in(seed_i, [f(S(i,j)) for j]) for i])`. The emitted
form:
- R[r] = seed_i for every r.
- Visit canonical cells (i ≤ j) in canonical order. Append z = f(pool cell) to R[i] by
  `R[i] = k_in(R[i], z)` (argument order kept: accumulator first), and also to R[j] if i ≠ j.
- When canonical row i ends, R[i] is complete. Rows complete in ascending i, so
  `out = k_out(out, R[i])` runs the outer chain unchanged.

The per-row operand sequence is exactly the source's (planning section A proof; 144 symbolic
cases). Because every chain sees the same operation tree, no licence is consumed, and any pure
kernel is admissible (not only `+`), with any invariant seed including -0.0.

**G-row-group generalization (new; this is what makes it vectorize).** Process canonical
rows i0..i0+G-1 together:
1. The G×G diagonal block in block-lex order, scalar: cell (a, b) feeds R[i0+a] and, if a ≠ b,
   R[i0+b].
2. Then for each column j ≥ i0+G, ascending:
   - feed the G row chains R[i0+m] their own z(i0+m, j);
   - feed the column chain R[j] the values z(i0, j) .. z(i0+G-1, j), in row order.
3. Commit R[i0..i0+G-1] to the outer chain, ascending.

Every chain still receives exactly its source sequence:
- R[i0+m] gets column contributions from rows < i0 (done earlier), then from rows
  i0..i0+m-1 (block-lex), then its own row.
- R[j] gets rows in ascending order.

Checked symbolically: `round2\A-work\group-scatter-check.js`, extents 1-13, G ∈ {1,2,3,4,6,8},
4×4 seed modes per level, 1,248 rank-2 cases all equal to the original nested tree. The
planning note "arbitrary traversal changes still require a per-row sequence proof" is met for
this traversal.

**Abort order (new).** For G = 1, the walk's first visit of canonical cell (a ≤ b) is the
logical cell (a, b) itself: mirror reads of a cell happen only in later rows. Restricted to
first visits, the walk order IS canonical lex order. So the first f evaluation that panics
(BL8013 / BL8014) is the same cell in both forms, and the same code and site are reported.
The same holds at rank 3: the sorted tuple is the lex-least permutation. This is why check 4
binds only G > 1.

### 2.3 Rank-3: slices + retained H (recommended first rank-3 form)

The leaf chain is H[a,b] = fold_leaf over c of f(S(a,b,c)). H[a,b] and H[b,a] are identical
operation trees (symmetric read, coordinate-free f, invariant seed), so one state per
unordered pair is CSE, not regrouping (planning proof). The planning event list per canonical
triple (deduplicated (pair, omitted-coordinate) events) is equivalent to this **slice form**,
which is what to emit. For each first coordinate i, a contiguous pool segment:
1. **The rank-2 group scatter over the slice triangle** {(j, k) : i ≤ j ≤ k}, with chains
   R[j] := H[i, j], value z = f(S(i, j, k)). This covers events H[i,j]←k and H[i,k]←j, with
   the dedup at j = k built in.
2. **An elementwise event over the slice:** H[j,k] ← z for every cell with j > i, k ascending.
   This is the third event; at j = i it is the duplicate of the column event and is skipped.
3. All H[i,·] complete at the end of slice i.

Then the retained H is a packed SymIdx<2, n> triangle. Run the rank-2 scatter over it with
f = identity, kernel = middle fold, seed = middle seed, and outer = outer fold. Checked
symbolically: `slice-scatter-check.js`, extents 1-9, G ∈ {1..4}, 4^3 seed modes, 2,304 cases.
A G ∈ {1,4} retained-H check covers 1,152 cases.

**Retained H first, not the online composition.**
- At p = 171 the online form saves only the 117,648-byte H, which is L2-resident.
- The middle / outer pass over H is 29,241 + 171 adds, against 2.5M leaf adds.
- The retained form reuses the rank-2 emitter verbatim.

Leaf adds become C(p+1,2)·p = 14,706 × 171 = 2,514,726, against 5,000,211 logical. The event
census per triple matches: 818,805 distinct triples × 3, plus 29,070 × 2, plus 171 × 1, which
is 2,514,726.

### 2.4 Bitwise claim and contraction

Prototype `round2\A-work\scatter_proto.cpp`: G = 4, column block 64, explicit `v4d` row
chains, g++ 16 `-O3 -march=native -fno-math-errno -fwrapv`.
- **`-ffp-contract=off`:** raw-bit equal to the walk in 176/176 cases. These cover n ∈ {1, 2,
  3, 5, 7, 13, 23, 37, 101, 171, 503}, seeds (+0, +0), (-0, +0), (-0, -0), (0.75, -1.25), a
  NaN cell, and all -0.0 cells.
- **`fast`:** walk vs scatter differ in 8/176 cases, by 1 ulp
  (`0x1.ac03d329498f8p+25` vs `...f9p+25`). The scatter's own fast vs off bits also differ in
  16/176. Both forms fuse `acc + x*x` per context (the memory note on context-dependent GCC
  contraction).

So the claim is the one the gates already define: **bitwise under `BLADE_FP_REPRO=1
BLADE_FP_CONTRACT=off`**. The bench repro regime runs `-ffp-contract=fast` with a 1e-12 pin
tolerance, so 1-ulp drift is inside it but is not "identical".

Optional v1.1 is contraction invariance:
- C++: route every term through the Z buffer and `BLADE_FOLD_ROUND` the scalar-path terms
  (the fold-lanes discipline).
- LLVM: emit the term's last `fmul` without `contract`, or use `llvm.arithmetic.fence`.
- That makes scatter(fast) equal walk(off), which is stronger than today's walk.

### 2.5 Vectorization rule (C++ shape that g++ vectorized in the prototype)

Vectorize ACROSS distinct fold states, never along one. Per row group, per column block of
B = 64:

```cpp
for (int m = 0; m < G; m++)                       // (a) term map, contiguous pool runs
  for (size_t jj = 0; jj < len; jj++) Z[m][jj] = f(P[rs[m] + (jb - (i0 + m)) + jj]);
for (size_t jj = 0; jj < len; jj++) {             // (b) column chains: lane = distinct R[j]
  double c = R[jb + jj];
  for (int m = 0; m < G; m++) c = k_in(c, Z[m][jj]);   // G ordered applications per chain
  R[jb + jj] = c;
}
for (size_t jj = 0; jj < len; jj++)               // (c) row chains: lane m = chain R[i0+m]
  rowv = rowv + (v4d){Z[0][jj], Z[1][jj], Z[2][jj], Z[3][jj]};   // one vertical add per column
```

- g++ reported loop (b) vectorized with 32-byte vectors (`scatter_proto.cpp:41`) and (c)
  vectorized as a basic block.
- No `#pragma omp simd`, `BLADE_OMP_SIMD_REDUCTION` or `BLADE_IVDEP` goes on any loop that
  carries a chain: a scalar chain would be licensed into a reduction.
- Without `-fassociative-math`, GCC cannot reassociate a scalar FP reduction, but the form
  above never offers one.
- Larger G (8, 16) gives 2-4 independent `v4d` chains per column, hiding add latency.
- Storing Z transposed ([jj][m]) for (c) is a tuning detail. G and B are speed-only (proved
  for any G) and belong in `BLADE_TILE_TUNE` (`scatter_g`, `scatter_b`).
- Rank-3 slices use the same three loops, plus a fourth elementwise loop
  `H[hs(j) + k - j] = k_leaf(H[...], Z[...])`, which is contiguous in k and vectorizable.

LLVM lane: emit the same structure as scalar IR with per-lane-distinct GEPs (clang's loop / SLP
vectorizer handles (b)), and `<4 x double>` `fadd` (no `reassoc`) for (c).

### 2.6 Can the scatter be threaded repro-safely?

**Not by row blocks.** R[j] receives contributions from every canonical row i < j. If thread
A owns rows [0, k) and thread B rows [k, 2k), then for j ≥ 2k the chain is
`seed + z(0,j) + ... + z(k-1,j) + z(k,j) + ...`. Per-thread partial chains combined as
`partA + partB` change that tree: `((s+a1)+a2)+(b1+b2) ≠ (((s+a1)+a2)+b1)+b2`. That is
reassociation, illegal in repro.

**An ordered-commit pipeline is legal but heavy.** B may apply its rows to column block c only
after A finished c, which is a 2-D wavefront over (row block, column block). It has
O(n/B_col) synchronization steps and triangular imbalance. Not proposed.

**Column ownership is legal**, but it is G35 itself, pulled instead of pushed. The owner of
chain R[r] reads (i, r) for i < r, then (r, j ≥ r). That is the logical row r in source
order: each row's complete fold runs on one thread.

So the scatter is the serial-repro answer and G35 is the mt8-repro answer. On rank 3, the
pull form has a sharing variant (G35 v2): each H[a,b] is an independent complete leaf chain.
Its owner reads c < a at (c, a, b), a ≤ c < b at (a, c, b), and c ≥ b at (a, b, c). That
gives 14,706 parallel chains and 2,514,726 reads instead of 5,000,211, then a cheap
middle / outer pass.

### 2.7 G35: threaded complete-row folds (repro)

**Input.** A Path B-eligible nest whose plan exists today (`chunkable` true:
`IsOmpParallel || mapLicensesOuter`, licensed kernel, rectangular head) in a build where
`fpReassocEnabled ()` is false and `ompThreadEmissionEnabled ()` is true. The outer level's
per-index value must be a complete inner fold: an `IRReduceCompute` / `IRReduce` / `IRProdSum`
in the body.

**Form.**
1. The team takes the SAME partition (`blade_fold::chunk_lo`). Each thread evaluates whole
   rows: `slot[i] = body(i)`. Each inner chain is the serial one, on one thread.
2. After the region, serially: `acc = seed; for i ascending: acc = k_out(acc, slot[i])`.

No chain is split, so it is byte-identical to the serial walk. The omp licence is used only
for its concurrent-term-evaluation half. Its reorder half stays declined: the licence is the
fold's own, nothing is transferred.

**Refusals.**
- Bodies that can panic (`blade_rt::panic` takes the first thread to arrive, which is racy).
  v1 admits the bodies the bench has: proven `range<I>` subscripts, no integer `/`.
- A static work gate: total inner cells ≥ 2^18 (`BLADE_TILE_TUNE rowslots_min`). This declines
  the input-checksum rows (pairkernel 2503 × 3, comoment3 171 × 201 = 34K), whose rows are a
  handful of adds.

**Lanes.** C++ only. LLVM has no threads (G11) and keeps its serial walk, or the scatter.

**Rank-3 note.** v1 threads only the outermost level (171 rows of 29,241 reads; static chunks
are within ~3% of balance at T = 8). v2 can slot the 14,706 (a ≤ b) leaf chains (2.6).

## 3. Target cells (RESULTS §14.8; ratio = expert / Blade)

| Cell | Blade ref (ms) | gf / ifx expert (ms) | Ratio | Repro excess (difference of medians) | Form |
|---|---|---|---|---|---|
| serial repro pairkernel | llvm 38.22, cpp 38.64 | 34.5 / 32.11 | 0.903 / 0.840 | minus serial licensed (29.15 / 29.52) = **9.1 ms** | scatter, rank 2, both lanes |
| serial repro comoment3 | llvm 15.30, cpp 15.54 | 11.82 / 11.93 | 0.773 / 0.780 | minus licensed (8.726 / 8.945) = **6.6 ms** | scatter, rank 3 |
| mt8 repro pairkernel | cpp 18.16 | 6.72 / 6.515 | 0.370 / 0.359 | minus mt8 licensed 7.541 = **10.6 ms** | G35 |
| mt8 repro comoment3 | cpp 10.82 | 5.171 / 4.615 | 0.478 / 0.427 | minus 2.973 = **7.85 ms** | G35 |

- The kernels are identical in both builds (the pairkernel map has no fold; the simplex tile
  needs no licence). So the excess is the checksum walk minus the orbit pass it replaced. The
  differences are across sessions (132026 / 142502 / 135344 / 145930) and are indicative.
- G32's diagnostic split for mt8 licensed pairkernel is ~5.4 ms triangular kernel plus
  ~1.5 ms threaded orbit checksum.
- **Parity thresholds** (how much must disappear, not forecasts):
  - serial repro pairkernel: 3.7 / 6.1 ms of the 9.1;
  - serial repro comoment3: 3.5 / 3.6 ms of the 6.6;
  - mt8 repro comoment3: 5.6 / 6.2 ms of the 7.85;
  - mt8 repro pairkernel: unreachable from the checksum alone. Even a free checksum leaves the
    licensed 7.54 ms (0.80), and parity needs a repro checksum at ~1.2 ms or below.

**Today's loops (emitted by 4136f0b5, `BLADE_FP_REPRO=1`).** Serial and mt8 emissions differ
only in the producers' pragmas; the checksum is the same serial code:

```cpp
// pairkernel, C++: per logical row (called 2503 times from the outer loop)
double __lambda_80(Pts i, Array<double, 2>& kmat) {
    double __ret1073741824 = 0.0;
    for (size_t __i0 = 0; __i0 < 2503; __i0++) { int64_t j = __i0;
        __ret1073741824 = __wrap_79___ret1073741824(__ret1073741824, ([&]() { const auto __bo = ([&]() -> double {
            std::array<size_t,2> __g0 = { (size_t)(i), (size_t)(j) }; bool __z0;
            int __p0 = nested_array_utilities::canon_fold<2>(__g0, false, __z0); if (__z0) return double();
            auto __c0 = nested_array_utilities::canon_left_justify<2>(__g0, false);
            double __v = kmat[__c0[0]][__c0[1]]; ... return __tv0; }()); return (__bo * __bo); }())); }
    return __ret1073741824; }
// outer:  // [omp] requested but emitted serial: BLADE_FP_REPRO=1 -- ...
for (size_t __i0 = 0; __i0 < 2503; __i0++) { int64_t i = __i0; __v60 = __wrap_81___v60(__v60, __lambda_80(i, __v33)); }
```

comoment3 C++ is the same pattern three deep (`__lambda_108` over k with `canon_fold<3>` and
`t[__c0[0]][__c0[1]][__c0[2]]`, `__lambda_112` over j, outer over i). The comoment3 LLVM leaf
(per logical cell) is:

```llvm
%r66 = icmp sgt i64 %r63, %r64 ... (3 compares, 6 selects: the sort network)
%r76 = load i64, ptr %r75            ; @.blade.ctab.0[a]   (G34 coordTerm table)
%r79 = load i64, ptr %r78            ; @.blade.ctab.1[b]
%r82 = load double, ptr %r81         ; pool[c + T0 + T1]
%r83 = fmul contract double %r82, %r82
%r88 = fadd contract double %r86, %r87   ; one dependent add per logical cell
```

**What disappears (counts).**

| | Walk | Scatter |
|---|---|---|
| pairkernel reads | 6,265,009 network-canonicalized reads (C++: two dependent pointer loads each; LLVM: network + table) | 3,133,756 contiguous pool reads, no canonical arithmetic |
| pairkernel strided reads | 3,131,253 off-diagonal mirror reads hit a different pool row each (25.07 MB pool) | none: the pool is streamed once |
| pairkernel f evaluations | 6,265,009 | 3,133,756 |
| pairkernel inner additions | 6,265,009 | 6,265,009 (every chain keeps its operands) |
| pairkernel latency-serial adds | 6,265,009 (one row chain at a time) | ≈ 3.13M / G vector-lane chains plus throughput-bound column adds |
| comoment3 reads | 5,000,211 logical | 848,046 |
| comoment3 leaf adds | 5,000,211 | 2,514,726 (shared H) |

comoment3 middle / outer adds are unchanged (29,241 / 171).

G35 removes no operations. It divides the walk over T threads with one 20 KB / 1.4 KB slot
array and one serial outer chain of 2503 / 171 adds.

**Which form serves which (build, licence) cell:**

| Build × fold licence | Today | v1 after A |
|---|---|---|
| default, all levels licensed (bench) | orbit-class fold | unchanged (orbit wins precedence) |
| default, every level unlicensed (`where repro` named kernel; non-builtin, non-comm lambda) | serial logical walk (`serial (no reorder licence)`) | **scatter** (serial, both lanes) |
| default, mixed licensed / unlicensed levels | walk (inner simd-reduction, outer serial) | unchanged in v1 |
| REPRO, any licences, `BLADE_OMP_THREADS=1` or no omp request (serial bench repro) | serial walk | **scatter** |
| REPRO, omp requested, threads on (mt8 bench repro) | serial walk + census | **G35** in C++; LLVM lane: scatter |
| default, `BLADE_ORBIT_FOLD=0` | walk | walk (the knob's documented meaning) |

## 4. Implementation design

### 4.1 OrbitFold.fs (shared plan, both lanes)

- Split `recognise` into:
  - `recogniseWalk` (:314-400: levels, read, group, pure f, static extents, level ranges),
    returning a `WalkShape` record (levels, read tuple, group, et, body', n, r);
  - `planLicensed shape` (today's :394-470, verbatim: the vetoes stay where they are);
  - a new `planStrict shape` that applies the checks of 2.1.
- `planStrict` returns `StrictPlan`, recording:
  - per-level `{Fold; Seed; AccTy}` (seeds hoisted);
  - `Rank`, `Extent`, `PoolCells`, `StateCells` (n or C(n+1,2));
  - `RowGroup` (G from `BLADE_TILE_TUNE scatter_g`, default 4; G = 1 when f is not total);
  - `TotalF`, plus a decline reason.
- `recognise` keeps its signature and result for the orbit callers. Add `recogniseStrict`
  sharing the walk.
- Plan text helpers `scatterForm` / `scatterEvidence` / `recordScatterTaken` /
  `recordScatterDeclined`.
- Knob `orbitScatterEnabled () = envFlag "BLADE_ORBIT_SCATTER" true`: a function, read per
  call.

### 4.2 C++ lane, CodeGenBinding.fs

- In `genReduceComputeBindingCore` (:3743-3764), after the orbit `Declined` / `NotOrbit` arms:
  try `recogniseStrict`; if taken, call the new **`genOrbitScatterCpp`**, a sibling of
  `genOrbitFoldCpp` :3562.
- **Pool access:** `nested_array_utilities::pool_base(g.data)`. Row starts come from
  `canon_count` (:968) as in the G32 branch.
- **Emitted shape:** sections 2.2 / 2.5. Rank 3 uses the slice loop, then a call into the same
  rank-2 generator over a local `H` buffer.
- **f and kernels:** f is rendered once via `exprToCppCtx` with `readVarId` bound (as
  `renderF` :3567 does). Kernels use the existing wrapper lambdas (`__wrap_*`), or inline
  `a + b` when `foldKernelBuiltinOp = Some IRAdd`. No pragmas on chain loops.
- **Precedence vs G35:** if `ompRequested && ompThreadEmissionEnabled ()` under repro, take
  G35 and record `ordered orbit scatter not taken: a thread-level row form (G35) is taken
  under the omp request`.

### 4.3 G35, C++ only

- Add `ChunkRowSlots` to `ChunkForm` (`IRStorage.fs:111-116`).
- In `chunkForm` (CodeGenBinding :3916), when `not (fpReassocEnabled ())`, the outer body is a
  complete fold, the body is total, and the work gate holds: choose `ChunkRowSlots`.
  `chunkable` is unchanged.
- **CodeGenLoopNest:**
  - prologue (:1612-1640): allocate `T* __rslot = new T[rn]`;
  - fold statement (:2132): `__rslot[i] = <body>` instead of `acc = wrap(acc, body)`;
  - epilogue (:2189): replaces the thread-order partial combine with a serial ascending loop
    from the seed;
  - census (:1807): no repro-suppressed line when `ChunkRowSlots`.
- **Plan:** `fold-form` `threaded complete rows (repro): N row folds into indexed slots, the
  outer chain serial in ascending order from its seed`, plus an applied `omp` record.
  `CliCommands.llvmLaneDecision` (:988) rewrites it for LLVM to the scatter / serial decision.
- Knob `BLADE_FOLD_ROW_SLOTS` (default on).

### 4.4 LLVM lane, EmitLlvm.fs

- `emitReduceCompute` (:6107): after `tryOrbitFold` returns None, call a new
  `tryOrbitScatter`, a sibling of `emitOrbitFold` :6006, with the same flat-read admission.
- Loops use `emitCountedLoop`-style helpers, `<4 x double>` row-chain `fadd` without
  `reassoc`, and per-instruction `contract` as today.
- No threads. In mt8 repro the LLVM lane takes the scatter, the C++ lane G35.

### 4.5 Interpreter twin

- No change. The interpreter runs the original IR, which is the specification.
- `blade test interp` (REPRO + CONTRACT=off) becomes the scatter's and G35's differential on
  day one. Every orbit corpus file (symmetry/112-115, 123, 124 and the bench-shaped ones)
  takes the scatter, or G35 where `where omp` is spelled (the suite leaves threads on), in the
  C++ lane under repro.
- The change is emission-only, so `opt-diff` cannot see it and needs nothing. Do NOT put
  either knob in `optimizerGates` (that list means optimizer passes).
- Register both knobs in `suiteClearedKnobs` (CliSelfTests.fs:93). The exe cache keys on
  emitted text for free.

### 4.6 Tests

**Corpus (`tests/corpus/symmetry/`).** The default suite is licensed, so strict cells are
reached through named `where repro` kernels: `function radd(a: Float64, b: Float64) where
repro -> Float64 = a + b`. A probe confirmed it plans `serial (no reorder licence)`; a `where
repro` lambda is BL3999.

- **`128_ordered_scatter_sym2_strict_kernels.blade`:**
  - p = 5 and p = 7 SymIdx<2> pools with float data;
  - checksums with `radd` at both levels, seeds (-0.0, 0.0) and (0.75, -1.25);
  - a cancellation design whose nested tree and any flattened / orbit-regrouped tree print
    differently at 15 digits (rows `1e16 + 1` and `1 + 1`, then a `-1e16` row: nested 2.0 vs
    flattened 0.0);
  - one `(+)` licensed twin (orbit) for contrast.
  - `// EXPECT:` values from the interpreter.
- **`129_ordered_scatter_sym3_strict_kernels.blade`:** p = 5 and 7, three distinct level seeds
  (0.5, -0.0, 1.0), `radd` kernels, plus a non-`+` pure kernel
  (`lambda(a, b) -> b - a * 0.5` with explicit seed: unlicensed).
- **`130_ordered_scatter_nan_signed_zero.blade`:**
  - a NaN canonical cell (producer `if`) gives a NaN checksum;
  - all -0.0 cells with -0.0 seeds give -0.0, pinned with `1.0 / ss = -inf`;
  - +0.0 seed gives 0.0.
- **`131_ordered_scatter_refusals.blade`:** each refusal still computes the right value via
  the walk:
  - AntisymIdx pool;
  - coordinate-dependent body;
  - seed mentioning `i`;
  - impure / partial kernel (integer `/` in an Int64 kernel);
  - rank 4.

**Raw-bit block, new `blade test orbit-scatter`.** Dispatch in `CliSelfTests.fs`, appended to
the default suite in `RunAll.fs`, modeled on `runFoldLanesTests` (LinAlgTests.fs:1676).
- A generated program under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, in both lanes
  (`BLADE_LLVM` unset / 1).
- Pools: rank 2 at p ∈ {1, 2, 5, 7, 13, 23, 37}; rank 3 at {1, 2, 5, 7, 11}.
- Bodies `x*x`, `x`, `abs(x) - 0.25`, `x*x*x`. Seeds +0 / -0 / 0.75 per level.
- Each scatter binding `a` has a walk-forced twin `b` that reads the second coordinate order
  (`g(i,j) * g(j,i)` is bitwise x*x, and both forms decline it). The program prints
  `eq = (a == b) || (isnan(a) && isnan(b))` and, for zero results, `1.0 / a == 1.0 / b`.
  Every `eq` must print `true`.
- Repeat with `BLADE_TILE_TUNE=scatter_g=1|3|4|8`: output text identical.
- `BLADE_ORBIT_SCATTER=0` vs on: identical text.
- G35 leg: same program with `where omp` outer folds, threads on vs `BLADE_OMP_THREADS=1`:
  identical text, and `eq` true against a serial-only twin.
- Under contract=fast: no bit assertion in v1. If v1.1 lands, assert scatter(fast) ==
  walk(off).

**Plan needles (`tests/OptimizeTests.fs`, beside :1660-1715):**
- `plan_orbit_scatter_sym2_applied_repro` (`withGate BLADE_FP_REPRO 1`):
  `"ordered orbit scatter (rank 2"`, `"row groups of 4"`.
- `plan_orbit_scatter_sym3_applied_repro`: `"(rank 3"`, `"shared pair chains 28 of 49"` at
  p = 7.
- `plan_orbit_scatter_unlicensed_default` (`radd`, no repro): applied.
- `plan_orbit_scatter_declined_knob`, `_declined_antisym`, `_declined_mixed_licence`,
  `_declined_partial_kernel`: declined lines.
- `plan_orbit_fold_declined_repro` (existing) still holds and is now followed by the scatter
  record.
- G35, in `tests/OmpTests.fs` beside :1567:
  - `row_slots_threaded_repro`: `fold-form` plus `omp` applied, C++ contains `__rslot`;
  - `row_slots_threads_suppressed`: `BLADE_OMP_THREADS=1` gives the scatter;
  - `row_slots_declined_light_rows`: input-checksum rows.

**Docs.**
- CLAUDE.md env-table rows: `BLADE_ORBIT_SCATTER`, `BLADE_FOLD_ROW_SLOTS`. Append to
  `BLADE_ORBIT_FOLD` ("under REPRO the ordered scatter / G35 take the walk").
- `BLADE_TILE_TUNE` keys `scatter_g`, `scatter_b`, `rowslots_min`.
- Section in `docs/plans/plan-compact-sym-folds.md`; `docs/features.md` row.
- gap-register G35 entry and a new scatter row.

## 5. Effort, risks, drop experiments

**G35: effort S-M.** One `ChunkForm` arm, about five emitter edits, plan and census, OmpTests
cases.
- Risks:
  - the panic race (gate on total bodies);
  - LLVM vs C++ plan divergence, handled in `llvmLaneDecision`;
  - the pairkernel column part is memory-strided, so scaling may saturate bandwidth before 8x.
- **Drop if:** an OLD vs NEW A/B on mt8 repro (C++, n = 21 interleaved, medians) shows the
  checksum's share falling by less than 3x at T = 8. Measure with a `BLADE_PHASE`-style split,
  or total minus the mt8 licensed kernel.

**Scatter: effort M (rank 2) + M (rank 3) in both lanes; L total with tests.**
- Risks:
  - recognizer refactor regressions in the orbit fold (mitigated by keeping `planLicensed`
    byte-for-byte);
  - per-lane vectorization parity ("LLVM must not lose to C++");
  - kernel purity checks too narrow or too broad;
  - contraction drift in the fast default (2.4);
  - rank-3 H indexing off-by-ones (the symbolic checks pin the order; the raw-bit block pins
    the values).
- **Drop experiment (do it before the lane work):**
  1. Extend `round2\A-work\scatter_proto.cpp` with the emitted walk copied verbatim
     (`canon_fold` plus nested pointers, from `pairkernel.repro.serial.cpp`) and a rank-3 slice
     prototype.
  2. Time walk vs scatter on the real shapes (n = 2503 SymIdx<2>, n = 171 SymIdx<3>),
     interleaved, n = 21 medians, contract=off. Raw bits are already checked.
  3. **Drop rank 2 (rank 3)** if the scatter checksum does not come in below 50% of the walk's
     time. At that point it cannot remove the 3.7-6.1 ms (3.5-3.6 ms) that parity needs from
     the ~9.1 (~6.6) ms excess.

## 6. File overlap with the other round-2 items

- **`src/OrbitFold.fs` (recognizer split, new strict plan):**
  - **B:** licensed compact-producer checksum epilogue S11 on pairkernel will read the same
    walk shape.
  - **D:** pairkernel mt8 team reuse, if it touches the G32 orbit threaded branch.
  - Recommend A lands the `recogniseWalk` / `planLicensed` / `planStrict` split first, or B
    rebases on it.
- **`CodeGenBinding.fs genReduceComputeBindingCore` :3712-3764 (dispatch) and
  `genOrbitFoldCpp` neighbourhood:** B (S11 epilogue dispatch); C (sibling-fold sharing goes
  through `genReduceJoinCore` and the Path B plan at :3897-3931).
- **Path B machinery** (`chunkForm` CodeGenBinding :3916-3931, `ChunkForm` IRStorage.fs:111,
  CodeGenLoopNest :1560-1640 / :1807 / :2132 / :2189): **C** (sibling folds in a threaded team)
  and **E** (thread-level `<&!>` join form, omp census phrases). This is the highest-conflict
  area for G35.
- **`EmitLlvm.fs emitReduceCompute` :6107 / `tryOrbitFold` :5992:** B (LLVM twin of S11), C
  (join legs).
- **Shared:** `CliSelfTests.fs` `suiteClearedKnobs` / dispatch, `CliCommands.llvmLaneDecision`
  :988, `tests/OptimizeTests.fs` plan cases, `tests/OmpTests.fs`, CLAUDE.md env table,
  `BLADE_TILE_TUNE` key parsing (D adds tile keys there too), `bench/notes/gap-register.md`.
- **Measurement overlap:** pairkernel mt8 is also D's cell (licensed). A's are the repro cells,
  but both change the same binaries.

## 7. Recommended waves and A/B protocol

**Wave 1:**
- **G35**, C++ only: implement, gate, measure.
- **Scatter drop experiment:** standalone prototype timing (5), no repo change. Can run in
  parallel with B / D, but must be sequenced against C / E on the Path B files.

**Wave 2 (after the wave-1 numbers):**
- **Scatter rank 2** in both lanes, if the prototype clears its bar, with the raw-bit block,
  corpus 128 / 130 / 131 and plan needles.
- **Scatter rank 3** (slice form + retained H) right after, with 129.
- **G35 v2** (rank-3 leaf-chain slots with H sharing) only if mt8 repro comoment3 remains
  below parity after G35 v1.

**Wave 3:**
- contraction-invariant terms (v1.1);
- antisymmetric extension (signed mirror events f(-x) at the correct chain positions, f(+0)
  for unstored diagonal cells; v1 refuses with census "antisymmetric storage: the ordered
  scatter covers SymIdx");
- per-row seeds;
- rank ≥ 4 via recursive slices;
- licensed-but-orbit-declined nests (an A/B, not a legality question).

**A/B, harness standard (interleaved, medians, CI; OLD = 4136f0b5 binary):**

| Kernel | Regime | Arms | Expected movers | Controls |
|---|---|---|---|---|
| pairkernel | mt8 repro | blade-cpp | G35 | gram / antisym unchanged |
| comoment3 | mt8 repro | blade-cpp | G35 | gram / antisym unchanged |
| pairkernel | serial repro | blade-llvm + blade-cpp | scatter | unchanged elsewhere |
| comoment3 | serial repro | blade-llvm + blade-cpp | scatter | unchanged elsewhere |
| all 12 kernels | serial / mt8 licensed | -- | unchanged (orbit precedence) | -- |

- Gates before timing: `blade test interp` (whole corpus), `blade test orbit-scatter`,
  `blade test llvm`, `blade test omp`, `blade test optimize`, `blade test symmetry`, and the
  full default suite with the skip count checked.
- Plan lines for the four target cells are confirmed in the run record. Checksums must match
  the pins at 1e-12; under contract=off they must be byte-identical to OLD's.
