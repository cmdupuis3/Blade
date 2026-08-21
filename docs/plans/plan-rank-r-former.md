# The rank-r symmetric former — BLAS-class contraction without decompaction

**Status 2026-08-21: DESIGN, nothing built.** Synthesized from a five-agent pass
(algorithm, compiler integration, two repo censuses, external prior art) plus
direct verification of the claims that were checkable without building. Companion
to `plan-simplex-blocked-compute.md` (whose §0a-§0c measurements bound what is
left to win) and `plan-compact-sym-folds.md` (whose BL3999 refusal is adjacent but
distinct).

The object: **C(i₁ ≤ … ≤ i_r) = Σ_t A(t, i₁)·…·A(t, i_r)**, output packed
symmetric. At r = 2 this is `syrk`; at r = 3/4 the coskewness/cokurtosis tensors.
Blade already spells these (`ppl.moments`, `object_for(comoment)`, and the
hand-written fiber kernel) — none of them fast at rank ≥ 3.

---

## 0. Executive verdict

**The former does not need bricks, and that is the whole design.** The packed
pool's last coordinate is affine (`SimplexBlocksCore.prefixTerm` collapses to
`i - lo` at the last level), so for a fixed canonical prefix (i₁ ≤ … ≤ i_{r-1})
the cells i_r ∈ [i_{r-1}, n) are **one contiguous pool run**. That reduces the
rank-r former to a rank-2 problem with ragged-but-contiguous rows:

```
C[m, k] += Σ_t  G[t, m] · A[t, k]        m over the packed rank-(r-1) simplex
                                          G = row-wise Khatri-Rao power of A,
                                              restricted to canonical prefixes
```

Call it the **KRS schedule** (Khatri-Rao Simplex). Because
`Σ_m (n − i_{r-1}(m)) = C(n+r−1, r)` exactly, KRS realizes **100% of r! in
flops** — the thing BCSS explicitly did not achieve.

Five consequences, in the order they should change plans:

1. **r! = r × (r−1)!, and the halves are bought by different mechanisms.**
   `(r−1)!` is free from canonical prefix enumeration; the remaining factor `r`
   comes *only* from the ragged tail. Overhead against the ideal: full
   decompaction pays `r!`; canonical-prefix-with-dense-tail pays `r`;
   all-dense bricks pay `∏(1 + k/T)`; **KRS pays `1 + r/(2(n+r−1))`** — 2.4% at
   r = 3, n = 61, and 0.3% at n = 500.
2. **The bottleneck is not bandwidth — it is one dependent FMA chain.** The
   measured comoment3 nest (`plan-static-array-erasure.md` §3b, 16.3 ms at
   61 × 2003) runs ≈ 14.6 GFLOP/s ≈ 23% of AVX2 peak, and a latency-only model
   of a single accumulator predicts ≈ 12 GFLOP/s. The `prodsum` inner fold is
   serialized on its own accumulator.
3. **So the cheap win is real and comes first**: unrolling that fold into ~4
   accumulators should recover roughly *half* the available speedup with no new
   schedule at all. KRS's own margin *over* that is ≈ 3.9×; the combined
   prediction is **4–6× on comoment3** (16.3 ms → 2.8–3.6 ms). All predicted,
   none measured — see §5's gates.
4. **S1 brick-major layout is NOT required.** KRS blocks the prefix and
   contraction axes and never the pool's contiguous axis, so its write runs are
   exactly as long as the serial control's. It therefore does not repeat the H1
   pool-write-scatter failure that sank S0 (`plan-simplex-blocked-compute.md`
   §0, third measurement).
5. **Keep the r = 2 `syrk` route.** For KRS to win at r = 2 it would have to
   reach 51% of tuned `dsyrk`'s rate, which it will not. At r = 3 it needs 17%,
   at r = 4 only 4.3%. **The crossover is at r = 3** — which is exactly where
   no BLAS routine exists.

**Why a compiler can do this and a library cannot.** BCSS (Schatz, Low, van de
Geijn, Kolda, SISC 2014) got the storage saving but lost the flop saving, and
said why: *"Most time is spent in the permutations necessary to cast computation
in terms of the BLAS matrix-matrix multiplication routine dgemm."* Their §6 shows
the full reduction *is* reachable — the tax is round-tripping each block through
an unpacked temporary to reach a fixed kernel. RFP + `DSFRK` escapes it at r = 2
only because that layout is already gemm-ready. A library must repack because it
calls a *fixed* microkernel; **a compiler generates the microkernel, so it can
read and write the packed layout directly and never materialize the temporary.**
That is the entire thesis, and it is why this is worth building here rather than
filing as a feature request to OpenBLAS.

---

## 1. What exists, and the gap it leaves

| Former | Ranks | Lanes | Routes to BLAS? |
|---|---|---|---|
| `gram(A, B)` | **2 only** (typecheck: `GramNeedsRank2`) | C++, interp; **no LLVM arm** | Yes, always — `?syrk`/`?herk` same-array |
| `prodsum(x₁…x_k)` | k-ary, but every operand rank-1; compact refused | C++, LLVM, CUDA, interp | Only k=2, only inside the `BlasL3` nest shape |
| `ppl.moments(A, k)` | k = 1..arbitrary | all (elaborates to plain Blade pre-typecheck) | No — the fast single-pass path is a **fold**, which `BlasL3` excludes by definition |
| `ppl.comoments(A, 2)` | **2 only** (order > 2 is an explicit error) | all | No — kernel body is a `Block`, not `ProdSumScaled` |
| `object_for(comoment) <@> (A,A,A)` | **2, 3, 4 proven** (`arity/031`) | C++ | No — generic arity-poly recursion |
| fiber kernel `method_for(A,…,A) <@> lambda where comm -> prodsum(…)` | any rank | C++, LLVM (packed pools at any rank, 2026-08-19) | **r = 2 only** |

**`LinAlgPatterns.(|BlasL3|_|)` already matches the rank-2 former nest** — on the
built loop nest, not the IR node — and routes it to `blade_gram_same_*`. It fires
today on `sgs/005`, `symmetry/017`, `ppl/065`. Its restriction is a hard
two-level guard (`cg.Bindings = [l0; l1]`), and `LinAlgPatterns.fs` states
plainly that a genuine three-level contraction "has no pattern here yet."

**So recognition is nearly free and the gap is precisely at r ≥ 3**: widen the
matcher from 2 levels to r, and route to a *generated* kernel, because there is
no library routine to call.

**A fixture bug this census turned up.** `reduce(x*y, (+))` is *not* `IRProdSum`
— nothing rewrites one into the other — so `bench_sym_gram.blade` never had a
BLAS route available regardless of `OPENBLAS_DIR`, and every "llvm beats C++ on
gram" number was measured against a C++ lane that was never going to classify.
`symmetry/017`'s own comment calls `prodsum(a, b)` *"the idiom-preferred form; a
hand-rolled zip + index reduce is the Python-bias trap."* **Fix the fixture
spelling before any former measurement.**

**And the r = 2 baseline is not what it looks like:** `blade_gram_same_d`
(`src/cpp/blade_linalg.hpp:239-249`) stages a full `m×m` `Cfull`, calls
`cblas_dsyrk` into it, then repacks the upper triangle into Blade's packed rows.
At m = 6006 that temp is 288 MB. So the shipping r = 2 route *already pays a
decompaction* — the very tax this design exists to avoid, and a real part of why
`DSFRK` (RFP output, no temp, exported by the OpenBLAS on this machine) is the
better r = 2 target.

---

## 2. The KRS schedule

For each canonical prefix m = (i₁ ≤ … ≤ i_{r-1}):

- the tail run is `i_r ∈ [i_{r-1}, n)`, contiguous in the pool, length `n − i_{r-1}`;
- the prefix contributes one **Khatri-Rao row** `G[t, m] = A(t,i₁)·…·A(t,i_{r-1})`,
  computed once per prefix and reused across the whole tail run;
- the update is a rank-1 accumulation over t: `C[m, i_r] += G[t,m] · A(t,i_r)`.

That is a gemm inner shape with a ragged row length. Register blocking is the
standard one: hold an accumulator tile over (prefix × tail), stream t, broadcast
`G[t,m]`, and FMA against a contiguous `A(t, ·)` vector. **Multiple accumulators
are mandatory, not optional** — finding 2 says the current single-chain fold is
the actual wall.

**Arithmetic intensity, confirmed but reframed.** At accumulator budget S, gemm
reaches `√S/8` flops/byte and the former `S^{(r−1)/r}/(4r)` — 8.0 / 21.3 / 32.0
at r = 2/3/4 with S = 4096. The surplus over gemm is real (2.7× at r=3, 4× at
r=4), but *all three already exceed machine balance*, so the surplus buys
**smaller tiles**, not more FLOPS. The higher-order former is compute-dense; its
O(n^r) output is an instantiation cost, not evidence of a bandwidth-bound loop.

---

## 3. Recognition and legality

- **No new IR node.** `LinAlgPatterns` is explicitly "match the built nest, don't
  invent a node"; widen `(|BlasL3|_|)`'s `[l0; l1]` to r levels.
- **Kernel legality is `ProdSumScaled`** — the existing predicate — following the
  `foldKernelBuiltinOp` precedent. This matters because in Blade the former's
  combining operation is *user code*: `comoments`' kernel is a `Block` (centering
  means), `object_for(comoment)`'s is recursive. Only the bare product-sum shape
  is legally re-schedulable.
- **The blocked/KRS former needs NO reassociation licence and changes NO bits.**
  It partitions *output* cells and re-groups the prefix product; each output
  cell's `Σ_t` still accumulates in order. This keeps the byte-identity gates
  green and lets the route be default-on. (Accumulator unrolling in §5's P1 is a
  *different* matter — that one does reassociate the t-sum and is licence-gated.)
- **Trap: `comm` is not a contraction licence.** `foldReorderLicensed` is
  *vacuously true* on every former nest. Reading it as authority to reorder the
  t-axis would silently change values. The symmetric-iteration licence and the
  contraction-reassociation licence are different facts.
- **Mechanics:** `SimplexBlocksCore.fs` compiles ~90 entries *after*
  `LinAlgPatterns.fs` and must move up (safe — zero opens); the licence
  predicates also compile after it, so they pass in as parameters exactly as
  `(|BlasL3|_|)` already takes `ompRequested`.

---

## 4. What NOT to do

- **Do not route blocks through `dgemm` via temporaries.** That is precisely
  BCSS's measured failure, and at r ≥ 3 there is no `dgemm` to reach anyway —
  the former is r-linear in t while `gemm` is bilinear. The only BLAS-shaped
  route is a per-brick Khatri-Rao expansion needing B²·d scratch, which also
  changes bits.
- **Do not pursue "fewer multiplications" (Solomonik–Demmel symmetry-preserving
  / Strassen-flavoured).** Real mathematics, wrong machine: it trades multiplies
  for adds, which is free-of-charge only where multiplies cost more —
  complex arithmetic, or communication-bound distributed settings. On FMA f64
  hardware it buys nothing.
- **Do not build S1 brick-major layout for this.** §0 finding 4: KRS never
  blocks the contiguous axis. S1 remains open for *other* shapes; it is not on
  this critical path, which spares the ten consumers of the lex-ascending packed
  order and the two agreement gates that pin it.
- **Do not touch the binop fast paths.** Same-shape compact elementwise
  (`S1+S2`, `cos(S)`, `S*2.0`) is already a flat pool walk with zero coordinate
  math in both lanes. §0c measured maps at ~1.75 cycles/cell with the memory
  system adding 7% — there is no prize there. The one binop family where
  blocking could pay is **mirror expansion** (compact → dense), the only strided
  read in the group.
- **Do not assume `emitCompactFold` is nearly done.** It exists and is
  unreachable (BL3999), but it computes *canonical* semantics while
  `plan-compact-sym-folds.md` §4.3 recommends *full-domain* — so it would need
  rewriting, not unblocking.

---

## 5. Phasing — each phase independently measurable, independently abandonable

| P | Deliverable | Gate | Why this order |
|---|---|---|---|
| **P0** | **Census, no code.** Which real surface forms actually build a matchable nest? Confirm `ppl.moments(A,3)` / `comoments` / `object_for(comoment)` emission shapes; fix `bench_sym_gram`'s `reduce(x*y,(+))` → `prodsum(x,y)` spelling | ≥1 real program classifies. **If nothing does, the routing has nothing to hook and the design stops here** | Zero compiler code; can kill the design in a day |
| **P0b** | **Hand-written C twin** of the KRS rank-3 former vs the emitted comoment3 nest, at 61 × 2003 | KRS twin ≥ 2× the emitted nest | The methodology that stopped the S0 emitter from shipping a loss |
| **P1** | **Multi-accumulator `prodsum` fold** (~4 accumulators), licence-gated on `BLADE_FP_REASSOC` since it reassociates the t-sum | ≥ 1.5× on comoment3, byte-identity gates green with the licence off | Predicted to be *half* the total win, and it is nearly free |
| **P2** | Widen `(|BlasL3|_|)` to r levels; emit the KRS schedule for `ProdSumScaled` kernels, C++ lane | Beats P1 by ≥ 2× at r = 3; three-way agreement gate green | The design proper |
| **P3** | LLVM lane KRS (shares the `LinAlgPatterns` decision; own emission) | Parity with C++ lane KRS | Lane-neutral by construction |
| **P4** | r = 2: route to `DSFRK` (RFP output, no `Cfull` temp) | Beats the current staging route at large m | Independent of P2/P3; fixes a 288 MB temp |

Refusals at every stage: anything that classifies but is not yet emitted must
**refuse by name**, never silently fall back to a slower schedule — the
"fastest way is the only way" invariant, and the reason the blocked rank-3
schedule refuses today rather than running serial.

---

## 6. Open decisions for review

1. **Is the `r = 2` route worth changing at all?** P4 (`DSFRK`) removes a 288 MB
   temp but touches a shipping, tested path. Defer until P2 proves the design?
2. **Does `prodsum` become the blessed former spelling?** The corpus already
   calls `reduce(x*y,(+))` the trap, but the LLVM benchmark fixtures use it.
   Either fix the fixtures (P0) or teach the front end to normalize
   `reduce(mul, (+))` → `IRProdSum` — the latter is a wider change with its own
   licence questions.
3. **How far does `comm`-arity generality go?** `object_for(comoment)` is proven
   at r = 2/3/4 but its kernel is recursive user code, permanently outside
   `ProdSumScaled`. Do we want a second, wider legality predicate later, or is
   "formers are `prodsum`-shaped" the contract?
4. **Antisymmetric formers** — out of scope above. The strict simplex has no
   diagonal and the sign is exchange parity; KRS's tail run is still contiguous,
   so it likely carries over, but nothing here is measured for it.

---

## 7. Lane divergence found in passing

The C++ flat-elementwise gate excludes antisymmetric shapes by name
(`CodeGenLoopNest.fs:1995`) while the LLVM gate does not
(`EmitLlvm.fs:3246-3254`). Since the llvm differential (395/0) compares *values*
against the C++ lane and is green over the symmetry categories, this is a
schedule divergence rather than a correctness one — the two lanes take different
paths to the same numbers. Worth reconciling so the lanes' performance
characteristics do not silently differ; not urgent.

Separately, and *not* part of this design: an elementwise binop between rank-1
arrays of different extents silently reads out of bounds in **both** lanes and
passes `blade check` even with explicitly distinct index types. That is a
correctness bug of its own and is tracked separately.
