# tensorop — named-axis tensor-product operator action, three ordered mode contractions

Group A (structure the compiler should derive). Directory `bench/kernels/tensorop/`. Round
3, catalog workload N09 (`round3/research/C-workloads.md` §N09), with the Codex review's
corrections (C/1: the required outputs; C/8: storage; C/10: licences and regimes).

## Object and operation

X is an n1 x n2 x n3 tensor over the named axes (I, J, K); A, B, C are square factors over
I, J and K. The operator action Y = (A ⊗ B ⊗ C) X is specified as THREE ORDERED MODE
CONTRACTIONS, I first, then J, then K:

    T1(a, j, k) = Σ_i A(a, i) X(i, j, k)
    T2(a, b, k) = Σ_j B(b, j) T1(a, j, k)
    Y(a, b, c)  = Σ_k C(c, k) T2(a, b, k)

**Required outputs: the inputs X, A, B, C (stored) and Y (stored).** T1 and T2 are
intermediates (review C/1): every arm chooses their lifetime and layout freely, but the
FACTOR ORDER is the specification — no arm may contract K or J first, merge two factors
(e.g. form C ⊗ B), or use any other association of the triple product. Each cell of T1, T2
and Y is ONE sum over its contraction index; the per-cell order is fixed as ascending
contraction index from a +0.0 seed (the Blade baseline's chain, below), so `repro` arms
agree to the rounding of FMA contraction. Loop order, blocking, register tiling, storage
orientation and jamming DIFFERENT cells are open to every arm.

## Inputs

| array | shape (row-major as listed) | linear index k | seed | lo | hi |
|---|---|---|---|---|---|
| X | (n1, n2, n3) | (i·n2 + j)·n3 + k | 10000019 | 0.0 | 1.0 |
| A | (n1, n1) | a·n1 + i | 20000038 | 0.0 | 1.0 |
| B | (n2, n2) | b·n2 + j | 30000057 | 0.0 | 1.0 |
| C | (n3, n3) | c·n3 + k | 40000076 | 0.0 | 1.0 |

## Checksum

    checksum       = Σ_{a,b,c} Y(a, b, c)²
    input_checksum = Σ X + Σ A + Σ B + Σ C       (one sum per input, combined in that order)

All terms are non-negative; the input sums are exact in every order (multiples of 2⁻³¹,
total below 2²²). The oracle `validation/ref_tensorop.py` is an ORDERED scalar oracle, not
einsum: a Python loop over each contraction index, numpy broadcasting only over the
independent cells, every cell a +0-seeded ascending chain with each product rounded. (The
research sketch's first two chains were unseeded `reduce`s starting from their first
product; on this fixture — products of non-negative finite inputs — that is the same bits,
since +0.0 + p == p exactly; the committed source and the oracle both use the +0 seed.)
Every arm's Y agrees with it to ≤ 5e-15 relative per cell at smoke size (FMA contraction is
the whole difference); every arm's checksum agrees with the pin within 5e-16 in every regime
of the smoke check (fastmath included).

## Sizes

| variant | n1 | n2 | n3 | X (= T1 = T2 = Y) | factors | FMAs (I, J, K) | est. serial (planning aid) |
|---|---|---|---|---|---|---|---|
| smoke | 11 | 13 | 17 | 19 KB | 3.8 KB | 27K + 37K + 70K | < 0.2 ms |
| full | 89 | 97 | 101 | 6.98 MB | 0.22 MB | 77.6M + 84.6M + 88.1M = 250M | Blade 0.2–0.6 s; expert 20–60 ms |

No extent is a power of two. **Storage estimate per arm (full size; payload + scratch,
review C/8):**

| arm | arrays | scratch | peak estimate |
|---|---|---|---|
| blade-cpp | X, T1, T2, Y, factors | each `m.mode_product` call writes a flat work buffer (6.98 MB) and reshape-copies it into the returned tensor, freeing the buffer at return; T1 and T2 live to the end of `kernel` | 34.9 MB (during mode K: X, T1, T2, work buffer, Y) + 0.22 MB |
| blade-llvm | as blade-cpp (same synthesized functions) | not audited beyond the smoke run | ~35 MB |
| natural | X, T1, T2, Y, factors (+ an n1 plane-sum vector) | none | 27.9 MB + 0.22 MB |
| expert | X, T1, T2, Y, factors (C stored transposed) | an 8 x 6 register tile per call; no packing buffers | 27.9 MB + 0.22 MB |
| every inputonly twin | X, factors | none | 7.2 MB |

Input share (planning aid, not a measurement): ~0.9M generated cells against 250M FMAs —
about 1–3% of Blade's region today, but an estimated 5–15% of the expert's (20–60 ms
region, ~2–4 ms of generation). The rule is the input share (kernels/README.md §3.3, review
C/11), and against the reviewed arm it plausibly exceeds 5%, so **`inputonly` twins are
provisioned for every arm**; the pilot keeps them if it measures > 5%. R >= 11 (serial).

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (ordered textbook nests — a DEVIATION from the intrinsic natural)**: three loop
  nests in the specified order, each over the OUTPUT cells in row-major order with the
  contraction innermost and one scalar accumulator per cell (`s = 0; s = s + U(r, t) *
  T(.., t, ..)`, t ascending). Every array is the mathematical one with its index order
  reversed (`xf(k, j, i) = X(i, j, k)`, `af(i, a) = A(a, i)`, `t1(k, j, a)`, `y(c, b, a)`,
  ...), so the last mathematical index is contiguous, as in the Blade source. mt8: `!$omp
  parallel do` on the outermost loop of every nest, the fills, the checksum
  (`reduction(+:)`) and the four input-sum folds over their leading index (`reduction(+:)`).
  It is, loop for loop, the nest `m.mode_product` synthesizes (`MathDecls.fs` 649–672), so it
  is an ordered textbook CONTROL with the library baseline's exact arithmetic.
  **Recorded deviation:** the catalog's N09 natural is `matmul` per slice, and
  kernels/README.md §1 says the natural tier keeps the intrinsic where the intrinsic is the
  natural spelling (gram keeps `matmul`). The W1-5 item brief asked for textbook nests and
  this commit follows it; three explicit `matmul` calls per slice CAN preserve the factor
  order I → J → K, so the earlier "hides the factor order" reason was wrong and is withdrawn.
  **Open for the Fortran tier-selection review, before publication:** assess the
  slice-`matmul` candidate (mode I as `matmul` of the (n2·n3) x n1 view by Aᵀ, mode J per a,
  mode K as C times the n3 x (n1·n2) view) against the per-cell ascending-chain requirement
  — whether libgfortran's `matmul` / ifx's inlined `matmul` keep each cell one +0-seeded
  ascending chain (blocking that splits a cell's k-sum would not) — and decide which is the
  natural arm; the textbook nests stay as the control either way.
- **expert (GEMM-shaped register tiles, same factor order)**: flat storage in the
  mathematical row-major order (C stored transposed, generated in that orientation). Each
  contraction is the column-major product `out(r, q) = Σ_t p(r, t) w(t, q)` on views of the
  flat arrays, no copies: mode I = X viewed (n2·n3) x n1 times Aᵀ; mode J = per a, T1(a)
  viewed n3 x n2 times Bᵀ; mode K = C times T2 viewed n3 x (n1·n2). One routine, an 8 x 6
  register tile (two ymm rows x six columns = 12 vector accumulators; rows blocked by 240 so
  the p panel stays in L2), each cell ONE +0-seeded chain in ascending t — the tile jams
  different cells only, so the per-cell order is the specification's and the contraction
  loops carry no reduction directive in any regime. The checksum is a separate ordered pass
  per (a, b) row; each input sum is a separate pass in the source's grouping (X per (i, j)
  row, then rows, then planes; A, B, C per mathematical row, C's rows read through the
  transposed `cct`). mt8: `parallel do` over row blocks (mode I), over a (mode J), over
  column chunks of 48 (mode K), over a (checksum, `reduction(+:)`), the fills, and the outer
  loop of each of the four input sums (`reduction(+:)`).
  gfortran 16.2 keeps 10 of the 12 accumulators in registers (two spill; inspected in the
  `-S` output); ifx was not inspected.
- **Selection record**: baseline = the ordered textbook nests (the W1-5 item brief's
  choice, a recorded deviation from the catalog's slice-`matmul` natural; the tier review
  decides, above); expert = the tiled GEMM form (W1-5, 2026-10-20; not yet seen by the
  Fortran reviewer). Tried: a first tile
  version that shared its accumulator array with the edge path made gfortran keep all 12
  accumulators in memory (store-back every iteration); the full tile now has its own array.

## Blade idiom (the library census baseline)

The committed source is the LIBRARY CENSUS BASELINE — the existing `math` module spelling of
a mode product, measured as it is today — not the best-performing existing Blade spelling
(the licensed map spelling below is stronger in mt8 and in licensed).

```blade sketch
import math as m
let t1: Array<Float64 like I, J, K> = m.mode_product(x, am, 0)
let t2: Array<Float64 like I, J, K> = m.mode_product(t1, bm, 1)
let y: Array<Float64 like I, J, K> = m.mode_product(t2, cm, 2)
let s = reduce(method_for(range<I>) <@> lambda(a) ->
                   reduce(method_for(range<J>) <@> lambda(b) -> prodsum(y(a, b), y(a, b)), (+)),
               lambda(p, q) where omp -> p + q, 0.0)
```

`m.mode_product` (`src/math/compiler/MathElaborate.fs` 349–367, `MathDecls.modeProductDecl`)
synthesizes one serial nest per (shape, mode): the output multi-index outermost in
row-major order, the contraction innermost, `acc = 0.0; acc = acc + U(r, t) * X(.., t, ..)`
ascending t, a flat work buffer, then a reshape copy into the returned tensor. It reads the
DECLARED shapes, so every operand and result is annotated with its named index types. The
nest carries no licence slot and no `[omp]`/`fold-form` record; the C++ lane emits a plain
loop (g++ contracts the multiply-add, never reassociates it), the LLVM lane `fmul contract`
/ `fadd contract` with no `reassoc` flag. So the kernel is serial in every regime, and mode
I's inner chain strides n2·n3 doubles (X is 7 MB), mode J's n3; mode K's reads are
contiguous.

`blade plan` (C++ lane, this tree 66e04875, smoke build of the harness check):

- serial-licensed: `[fold-form v1] lambda in lambda in s / lambda in s: applied [fold form:
  simd K-lanes(8) self-dot; prodsum]` (the per-row checksum), `__v321 ... simd-reduction`
  (its outer folds), the input sums `K-lanes(8)` / `simd-reduction`; every `[omp v1]`
  declined by `BLADE_OMP_THREADS=1` with the census line; `[pool-reuse v1] __math_1/2/3:
  declined -- no candidate pool is both dead and unaliased ...`. No record at all for the
  three contraction nests.
- mt8-licensed: `[omp v1] ... applied [BLADE_OMP_PARALLEL_FOR; loop nest level 0]` for the
  four generator maps; the output checksum `__v321` and the four input sums `threaded(Path
  B chunks, simd-reduction within each chunk)`; the contractions serial (serial
  mode_product kernel; threaded preparation; checksum threaded in licensed / fastmath,
  serial in repro — the `kernel.json` parallel note).
- repro (serial and mt8): every fold `serial (repro)` (mt8: `[omp] requested but emitted
  serial: BLADE_FP_REPRO=1 ...`; the row-slot form declines `IRProdSum` / `IRReduce`
  bodies); generator maps threaded in mt8.
- `[sibling-folds v1]` declines both the input-sum spine and the result tuple (callee bodies
  name capturing lambdas). The LLVM lane accepts the source (no fallback).

**Alternative spellings probed (not committed).**

- `m.matmul(am, m.unfold(x, 0))` with annotated `Idx<n2*n3>` operands checks and runs and
  routes mode I to the GEMM (`[blas-routing v1] ... Gemm (L3, d)`, the native `dgemm_nn`
  tile with the BLAS gate off), but modes J and K need the mode-1 / mode-2 unfolding of T1
  and T2, i.e. a refold of the unfolded matrix — the math surface has no refold, so the
  unfold-matmul chain cannot express the whole operator without index arithmetic into an
  anonymous matrix.
- The research sketch's map spelling (`method_for(range<I, J, K>) <@> lambda(a, j, k) where
  omp(a: 1) -> reduce(method_for(range<I>) <@> lambda(i) -> am(a, i) * x(i, j, k), (+))
  |> compute`, likewise T2, and `prodsum(cm(c), t2(a, b))` for Y) plans all three
  contractions `BLADE_OMP_PARALLEL_FOR; loop nest level 0`, the inner folds licensed and Y's
  `prodsum` `simd K-lanes(8) dot`, and a pool reuse T1 → Y. It is the stronger spelling in
  mt8 and in licensed, and it carries DIFFERENT SOURCE PERMISSIONS: a thread licence on
  each contraction map's outer level (`omp(a: 1)`), and builtin-`(+)` inner folds that are
  reorder-licensed (the T1 / T2 chains may take lane / simd-reduction forms in licensed;
  Y's `prodsum` takes K lanes), where the mode_product chains carry no licence at all — so
  its licensed numbers are not the same arithmetic contract as the census baseline's, and a
  matched expert would gain the corresponding `simd reduction` permissions. Review C asked
  for the existing library baselines; it did not exclude this spelling. It is recorded here
  as the comparison variant for the later recognizer item (to be measured beside the census
  baseline then, with its permissions disclosed), not committed as this kernel's source.

**What this workload measures.** The headline gap is the absence of a contraction
recognizer: a builtin-`(+)` fold over one index of a map whose reads are affine in it (or a
`mode_product`) should either interchange to the per-cell-order-preserving outer-product
nest or route the strided view to the native 6x8 `dgemm_nn` tile (which never splits a
cell's sum; its KC blocks reload each cell's partial exactly — the seed distinction of an
unseeded `reduce` chain has to be justified there, review C/"GEMM preserves KC order").
That is a later item; this kernel is its census target.

## Regimes

licensed (serial, mt8), repro (serial, mt8), fastmath (serial, mt8). Licensed and repro
differ in the checksum and input-sum folds only (the contraction nests carry no licence);
fastmath lets the vendors vectorize the natural tier's contraction reductions and the
checksum by blanket permission — "vendor fast-math vs Blade's reassociation licence —
unequal permission sets, accuracy-checked at 1e-8".

**Matched-permission loops (licensed, expert tier, behind `BENCH_LICENSED`)**: the output
checksum's row sums (`simd reduction(+:w)`, matched to the licensed `prodsum` rows and the
licensed outer folds) and the four input sums (`simd reduction` on each input's innermost
row sums, the rows combined per input over its leading index, the inputs combined in the
source's order). **The contraction loops get none** — the Blade baseline's chains are
not licensed, and the expert's register tile keeps every cell's order anyway.

## Hypothesis

- vs natural: **EQUIVALENT** (serial; both run the same textbook nests — the natural arm is
  exactly the nest `m.mode_product` emits; gfortran -O3 may interchange or vectorize what
  Blade's g++ does not, which would show as a loss and is then a back-end difference to
  diagnose). mt8: natural threads its three nests, Blade's contractions are serial: **LOSS
  up to ~T x** on the kernel share.
- vs expert, serial (licensed and repro): **LOSS 3–10x** today. Mechanism: one
  latency-bound FMA chain per output cell with no jam (≈ 4 cycles per FMA), the mode-I chain
  striding n2·n3 = 9797 doubles through a 7 MB X and mode J striding n3, plus the per-call
  zero-fill and reshape copy (3 x 7 MB extra writes); against the expert's register tile at
  a large fraction of FMA peak on all three contractions. mt8: wider still (serial Blade
  kernel). After a recognizer routes the contractions to the native tile: about
  **equivalent** (the same GEMM shapes the expert hand-writes).
- fastmath: natural gains (vectorized reductions); Blade and expert unchanged; labelled
  unequal.

## Risks

- The natural tier might be the fastest arm if the Fortran compilers interchange its nests
  (GCC `-floop-interchange` at -O3) — reported as measured, investigated either way.
- The Blade kernel at full size runs ~0.2–0.6 s serial (planning estimate, unmeasured) —
  below the 2.5 s cap; mt8 Blade stays serial in the kernel.
- `y` inside `kernel` is never freed in the C++ emit (the two intermediates are; the leak is
  outside this item and reported) — it does not change the timed region beyond 7 MB of
  unreturned heap.
- The expert's tile is a first version: no packing, no prefetch, no ifx-specific tuning;
  the Fortran reviewer may replace it (the factor order and per-cell chains are the fixed
  part).
