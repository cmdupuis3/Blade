# Optimization-site survey — five read-only passes over the compiler

**2026-08-21. Audit of five parallel surveys** (folds/recurrences, linalg/contractions,
emission mechanics, irregular storage, elementwise/AD/ML), each read-only, each ranking
its leads by (estimated win) x (confidence) x (frequency in the corpus). Companion to
`README.md`, which carries the built kernels and their verdicts.

**Read the README's corrections before trusting an estimate here.** This census is the
input that chose what to build; several of its estimates did not survive being built
(the 13.95x jam headline was sample-major, the 116-reduce census contains zero jammable
sites, and the estimated repack win measured as a loss). Where this file and the
README's corrections disagree, the corrections are the measurement.

Two surveys grounded their claims in the **1483 emitted `.cpp`/`.ll` files** left in
`generated_cpp_tests/` by a prior suite run, so the census numbers below are observed
emissions, not inferences.

---

## 0. The convergent finding — three surveys, one deficit

**Blade's surface puts every contraction's fold INSIDE the kernel body.** So the
innermost *emitted* loop of any contraction is a serial single-accumulator dependent
chain (~0.25 FMA/cycle, unvectorizable), while the independent output axis sits
*outside* it contributing nothing to instruction-level parallelism.

- **All 21 default accumulator arms across both lanes and CUDA are single-accumulator.**
- The only multi-accumulator machinery is the K-lane form, gated behind
  `BLADE_FP_REASSOC` (default **OFF**, `src/CodeGenState.fs:498-501`) or `where omp`.
- Census: **116 serial statement-form reduces against 6 licensed ones**; 73 programs
  with a `prodsum` IIFE inside a nest; 22 with a `reduce` IIFE inside a nest; 21 gram
  fallbacks; 8 matmul fallbacks.
- Measured in-repo gap for that shape: **2.9x bandwidth-bound, 7.5x cache-resident**
  (`src/CodeGenBinding.fs:2617-2626`).
- Since BLAS is default-off, **this is the shipping path**, not a corner case.

**The fix is bitwise-exact and needs no licence.** Almost every fold sits inside an
enclosing *independent* loop (a row map, a per-frequency map, an output axis). Blocking
*that* loop — unroll-and-jam over the output axis — produces the same independent chains
while leaving each individual cell's summation order untouched. It changes the
interleaving of independent cells, never a cell's addition sequence. That matters
disproportionately here because the interp and diff-oracle gates are byte-exact: a
bitwise transform can be **default-on** where the existing licensed K-lane form cannot.

In-source precedent: the hand-written matmul emitter already rejected exactly this loop
order in writing, with a measurement (`src/CodeGenExpr.fs:2558-2604`).

---

## 1. Ranked leads

Kind: **K** = microkernel (a register-blocked inner loop), **P** = emitter policy
(scheduling, allocation, a missing guard). Both are valuable; they need different work.

| # | lead | today (file:line) | regime | win | conf | bitwise | kind |
|---|---|---|---|---|---|---|---|
| 1 | **Unroll-and-jam the output axis around an in-nest fold** | `CodeGenExpr.fs:2490,2531,217,1122,1130` — all single-accumulator, contraction innermost | latency | 2.5-4x | high | **YES** | K+P |
| 2 | **`reduce(x*y,(+))` never becomes `IRProdSum`** — heap allocate/free **per output cell** | `generated_cpp_tests/AD_Array_Dot_Via_Additive_Reduce.cpp:132-137` | wasted work + allocator | 5-20x | high | YES* | P |
| 3 | **Whole-rank fold copies its operand** — the `srcIsNamed` guard exists in BOTH sibling desugars but not here | `TypeCheckInfer.fs:2504` vs `:2174`, `:2358` | bandwidth + allocator | 3-4x | very high | YES | P |
| 4 | **`let rec` step body unfused** — 17 alloc/pass/dealloc triples for ONE RK4 step | `Lowering.fs:2311-2331` | allocator | 5-15x | high | YES | P |
| 5 | **C++ lane has no expression-position fused fold** — materializes the operand per output cell | `CodeGenExpr.fs:170-174` | wasted work | **3.6x measured** (§0: 829.9 vs 232.6 ms) | high | YES | P |
| 6 | **`decompact` runs `canon_fold` + `std::sort` PER CELL** though the nest emits ascending coords | `CodeGenExpr.fs:2182-2195` | wasted work | 30+ ops/cell -> ~2 | high | YES | K |
| 7 | **`group_by`->`reduce` never fuses** — materialize-gather pass THEN per-group reduce | `CodeGenBinding.fs:1986-2055`, refusal at `:2751` | bandwidth + wasted work | high | med | YES | K |
| 8 | **`group_by` does N mallocs, not one CSR pool** — despite holding `offsets[ngroups]` | `CodeGenBinding.fs:2035-2045` | allocator | med-high | high | YES | P |
| 9 | **Iliffe skeleton lifecycle** — one `new[]` per interior row, `count_leaves` walked twice | `nested_array_utilities.hpp:212,235-241,393-408` | allocator | 24-40% of timed region at r=4 | high | YES | P |
| 10 | **Halo carousel serializes stencils** — withholds `BLADE_IVDEP` for a ring dependence, but fires on *dense* sources where its own rationale does not apply | `CodeGenLoopNest.fs:1229-1372,1550-1551` | ILP | 2-4x | med-high | YES | K+P |
| 11 | **`ppl.dist(A,r)` skips the pooled single-pass path its 3 sibling formers take** | `PplElaborate.fs:612-646` vs `:368-377` | wasted work | 3x/7x/15x at r=2/3/4 | high | YES | P |
| 12 | **`RouteGramDistinct` has an entry point but NO nest matcher** | `LinAlgPatterns.fs:1288-1298` | routing hole | 10-40x (BLAS on) | high | n/a | P |
| 13 | **`solve` bakes no literal extent, heap `std::vector` for a 3x3** | `CodeGenExpr.fs:2882,2933` | fixed overhead | 5-30x at n<=8 | high | YES | K |
| 14 | **`mask`->`compound` compaction is a scalar branch-per-cell loop** | `EmitCpp.fs:71-83` | branch misprediction | good at ~50% selectivity | med | YES | K |
| 15 | **AD recomputes transcendentals twice** — `derivRule` builds a fresh `exp(u)`/`sin(u)` node the primal also computes | `GradCommon.fs:121-150`, `Grad.fs:217,303-317` | duplicated work | medium-large | med | YES | P |
| 16 | **ML `tensor_product`/`derive_poly` emit runtime gathers over COMPILE-TIME-KNOWN CG tables** | `MLElaborate.fs:237-307,350-426`, `WignerTables.fs:156-169` | compute + gather latency | large on the kernel | med | YES | K |
| 17 | **`let rec` zero-prefill is dead stores** — every cell later overwritten, lag guards explicit | `TypeCheckInfer.fs:9645-9665,9700-9712` | wasted work | one store pass | high | YES | P |
| 18 | **`std::function` for main-local functions** — 354/1483 programs declare one, **3** need the erased type; 4 call one *per cell* | `CodeGen.fs:1341`, `CodeGenBinding.fs:3521` | indirection | large in hot loops | high | YES | P |
| 19 | **Multi-accumulator gated on `BLADE_FP_REASSOC` even for INTEGER/BOOL folds** (exactly associative — no licence needed) | `CodeGenExpr.fs:219`, `CodeGenBinding.fs:2601` | latency | 2.5-4.8x | high | YES | P |
| 20 | **No non-temporal stores for large write-once fills** | `CodeGenLoopNest.fs:2361`, `EmitLlvm.fs:2965` | store bandwidth | 3.35x on a pure store stream | med | YES | K |
| 21 | **hosvd mode-Gram computes the FULL square**, serial acc, strided reads | `MathDecls.fs:579-597` | wasted work | 4-10x | med-high | YES | K |
| 22 | **No pool recycling in the shim** — repeated scopes re-pay first touch + free every trip | `blade_llvm_shim.c:80-99,141-148` | allocator | ~2.9 + 1.3 ms per repeat | high | YES | P |
| 23 | **SparseIdx/CompoundIdx original-coordinate reads go through `unordered_map::at()`** behind a virtual call | `index_types.h:104,185` | latency | moderate | med | YES | K |
| 24 | **`schedule(dynamic)` on triangular nests** where `static,1` is a perfect round-robin with zero dispatch | `CodeGenLoopNest.fs:551` | dispatch overhead | small | med | YES | P |

\* lead 2 is bitwise except for a `-0.0` first-product edge case.

---

## 2. Corrections the surveys made to THIS project's own records

Recorded because each was believed, written down, and wrong.

1. **`matmulDecl` does not exist and matmul is NOT `t`-innermost.** The native arm is
   already **i-t-j** with the output axis innermost, with `BLADE_RESTRICT` hoists,
   `BLADE_IVDEP`, and a byte-identity note (`CodeGenExpr.fs:2556-2606`). Someone fixed
   it. The residual is only a missing i-block. *(The stale claim lives in the
   `stride-layout-facts` memory note and was repeated into a survey brief.)*
2. **The C++ flat-elementwise gate does NOT exclude antisymmetric storage.**
   `codeGen.IsAntisymmetric` is the **Reynolds** flag, not a storage class
   (`IRStorage.fs:696` states the distinction), and the corpus contains emitted
   `antisym<2,4>` flat nests. The real divergence runs the other way: the **LLVM** gate
   excludes *dense* shapes (`EmitLlvm.fs:3255`). This corrects a "lane divergence"
   claim recorded in `plan-simplex-blocked-compute.md` §0b.
3. **The C++ lane does NOT pay `deallocate` on top-level pools** — `registerAlloc`
   no-ops on an empty scope stack (`CodeGenLoopNest.fs:3686`). This corrects
   §0c's consequence 2, which asserted the C++ lane "emits `deallocate`
   unconditionally and pays it". The 1.3 ms free cost is real for *scoped* pools, not
   for whole-program ones.
4. **`DSFRK` is not the cheapest fix for the gram staging temp**, and the temp's cost
   is the **memset**, not the megabytes. RFP is not Blade's packed layout, so routing
   there swaps one repack for another, and there is no `ZHFRK`, so complex keeps the
   temp regardless. A panel-blocked shim removes both costs at every precision. *This
   amends `plan-rank-r-former.md` P4.*
5. **`packed_syr_syrk.c` assumes a SAMPLE-MAJOR operand layout.** That premise was not
   recorded in `README.md` and is the largest unstated integration risk in the arm that
   README recommends building. Transposing operands to sample-major is precisely the
   unlock for the register-blocked contraction (lead 1).

---

## 3. Explicit nulls — checked, and not worth building

- **Multi-statistic fusion is already right.** `Reduce_Fused_Pool.cpp` emits 5 register
  accumulators in one traversal with shared peels. k leaves give k chains and Zen 3
  needs ~6, so the common `sum/sumsq/count` idiom (k=3) is **already at the read
  roofline**. The target is k=1, not the fused case.
- **Scan is correctly unbuilt.** No scan node exists in the IR, and the corpus's only
  long recurrence (`physics/44`, `Idx<48000>`) is a modular LCG with a `floor` —
  non-affine, so scan cannot touch it. Do not build one before the gate program exists.
- **RaggedIdx itself is optimal** — flat CSR pool, same shape as a dense flat loop. The
  waste lives in `group_by`, not in ragged storage.
- **OrbIdx / wreath decompaction** is mechanically the seam `mirror_transpose.cpp`
  already validates, and has **zero usage** in `examples/` including the 47-program
  physics corpus.
- **Units carry zero runtime representation** (erased at codegen). **Complex gram**
  routes to `cherk`/`zherk`; **complex Hermitian eigensolvers** route to LAPACK. There
  is **no FFT** in `src/spectra/` to survey — it is Lomb-Scargle, already optimized.
- **Vectorized libm** is a real unbuilt item but has **low actual incidence**: the
  high-transcendental-count examples are scalar/tiny closed forms (`ppl.dist_map`), not
  bulk array loops. The raw grep counts overstate it.
- **Counter-based RNG (Philox)** is large in principle but **changes the stream** — not
  bitwise, needs an interpreter twin. A semantics change wearing a performance costume.
- **The alloca induction variable is exonerated** — post-`-O3` forms are identical to
  clang's own (`plan-simplex-blocked-compute.md` §0b finding 4).
- **The flat elementwise map is done** (~1.75 cycles/cell, memory adds 7%).
- **Multi-accumulator on operand-supply-bound nests is a NET LOSS** (README claim 1).

---

## 4. Two experiments that need no new code

1. **The stencil A/B is already emitted.** The LLVM lane emits a plain 3-read stencil
   where the C++ lane emits the halo carousel — recompile both from
   `generated_cpp_tests/` and time them against each other (lead 10).
2. **Recompiling one emitted row-fold with `-fno-loop-unroll-and-jam`** tests lead 1's
   entire premise without touching the compiler.
