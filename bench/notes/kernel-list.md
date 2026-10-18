# Kernel list (final) — Blade vs gfortran / ifx

Architect's deliverable 1. Directory names are FINAL; do not rename. Each kernel lives in
`bench/kernels/<name>/` with `spec.md` (architect), `<name>.blade` (blade-A / blade-B),
`natural.f90` + `expert.f90` (fortran), `kernel.json` (harness). The per-kernel `spec.md`
is authoritative where this page and the spec disagree.

Conventions used below: `p`, `n`, ... are the SIZE parameters (see `bench/README.md` §4 for the
`SIZE` marker). "cells" = output cells. Flop and time estimates are back-of-envelope
(never reported as results). All working sets include the natural tier's dense arrays and
stay <= 64 MB. No extent is a power of two.

Regimes (fp axis per README §3 / D40: `licensed` headline, `repro` secondary, `fastmath`
optional — "strict"/"fast" below are the retired names, read strict → repro and fast →
fastmath): every kernel runs `licensed x serial x native` and `repro x serial x native`.
`mt8` applies to every kernel's Fortran arms except `maskagg` (serial by protocol); Blade
reads "no parallel form" for `recur` and `onepass`. `fastmath` applies to the fold-bearing
kernels marked `fast` below. `blas` applies to `gram` only. Licensed-regime hypotheses (corrected by blade-A F15 and Fortran review F2): `licensed`
differs from `repro` for ALL ELEVEN kernels (every input-checksum fold reassociates under
the knob; maskagg's output checksum too — its compaction alone stays sequential), and the
expert's matched `simd reduction` set includes every kernel's input-checksum folds (README
§3 table). The inner `prodsum` of gram/comoment3/antisym has no
threading slot but IS a reassociation-licensed fold — 4-lane simd reduction in the C++
lane, 16-lane FMA accumulators in the LLVM lane — so their licensed hypothesis vs expert is
equivalence to a modest loss (repro: the single-chain loss stands). Deferred folds (dot,
fusedchain, saxpy's checksum, onepass's join legs) reassociate in the LLVM lane only (F11),
so there blade-llvm is expected equivalent to expert while blade-cpp keeps a serial chain
and loses (3–5x on dot). Each spec lists the exact loops on which the expert may place the
matched `simd reduction`.

Eleven kernels: four in group A (structure the compiler derives), five in group B
(language-level structure), two controls. Six of the eleven are NOT expected to beat expert
Fortran (`stencil`, `recur`, `maskagg`, `dot`, `saxpy`, and `gram` in the strict regime).

Changes against the charter's candidate list (reasons in `notes/design-decisions.md`):

- **A5 `band` is DROPPED.** `reduce(method_for(range<Band>) <@> k, (+))` is refused at
  codegen (BL7004, "reduce over a deferred computation is not supported for
  ragged/grouped/compound/sparse inputs -- bind with |> compute first"), and the
  `|> compute` form builds a hashed `sparse_index_t` key table of O(card) entries
  (docs/plans/structural/06). Neither is a benchmarkable closed-form enumeration today.
  Recorded as a compiler finding (D1).
- **A4 `pairkernel` is a materialized kernel MATRIX**, not a pair sum. The pair sum over a
  `where comm` kernel is a fold over compact storage, refused at typecheck (BL3999, the
  canonical-cell fold has no spelling yet: docs/plans/plan-compact-sym-folds.md §4.4), and
  the `range<Pair>` domain route is the refusal above. Kernel matrices are what GP/SVM code
  materializes anyway (D2).
- **A3 `antisym` is the angular-momentum tensor** L = XᵀY − YᵀX (an inner product per
  cell), not the O(1)-per-cell `f(x_i)g(x_j) − f(x_j)g(x_i)`: the O(1) form is a ~5 ms
  memory-bound write under the 64 MB rule and would be noise (D4).

## Group A — structure the compiler derives

### `gram`
**Math.** X is a p x n matrix (p variables, n samples). G = X Xᵀ in Blade's orientation
(G(i,j) = Σ_s X(i,s) X(j,s)), a p x p symmetric matrix. Named output: G. Checksum:
Σ_{i,j} G(i,j)² over the full logical matrix.
**Sizes.** smoke p=61, n=31. full p=1601, n=1001 (packed 1.28M cells = 10.3 MB; dense
20.5 MB; X 12.8 MB; 2.57 GF packed, ~0.4–1.0 s serial). scale (optional) p=2003, n=1001.
**Tiers.** natural: `matmul(transpose(x), x)` or the full p x p triple nest, dense storage.
expert: triangle only (`do j = i, p` or outer-product order `do s; do j; do i = 1, j`,
vectorized over i, which keeps each cell's summation order), packed or dense-symmetric
storage, manual jam of several output cells.
**omp.** Blade: `lambda(x: T^1, y: T^1) where comm(x, y), omp(x: 1)` → `blade plan` shows
`[omp v1] applied [BLADE_OMP_PARALLEL_FOR_DYNAMIC; loop nest level 0]`. Fortran:
`!$omp parallel do schedule(dynamic)` on the outer (row) loop of the triangle;
natural: `!$omp parallel do` on the outer loop of its square nest (matmul form: natural has
no mt8 arm unless the compiler threads matmul — the Fortran agent records which).
**Regimes.** serial, mt8, fast (inner dot gets simd lanes under REASSOC), blas (dsyrk).
**Hypothesis.** vs natural: win 1.5–2x if matmul is inlined as a naive nest, parity-to-loss
if the intrinsic is a blocked library kernel. vs expert, strict: LOSS 1.5–3x — Blade emits
a 6-way jammed row-fold (dot form), the expert's outer-product order vectorizes over i
without reassociating. fast: gap narrows. blas: parity by construction (both call dsyrk).

### `comoment3`
**Math.** X is p x n (p variables, n samples). T(i,j,k) = (1/n) Σ_s X(i,s) X(j,s) X(k,s),
the raw third-moment tensor, symmetric in (i,j,k). Named output: T. Checksum: Σ over the
full logical p³ cube of T(i,j,k)².
**Sizes.** smoke p=13, n=11. full p=171, n=201 (packed C(173,3) = 848,046 cells = 6.8 MB;
dense 171³ = 5.0M cells = 40 MB; 0.51 GF packed ≈ 0.15–0.4 s; dense 3.0 GF ≈ 1–2 s).
**Tiers.** natural: triple nest over (i,j,k) with the sample loop inside, dense cube.
expert: `i <= j <= k` nest with hand-packed offsets (or outer-product order over s), jammed.
**omp.** Blade: `where comm(x, y, z), omp(x: 1)` → dynamic schedule on level 0. Fortran:
`!$omp parallel do schedule(dynamic)` on the outer i loop.
**Regimes.** serial, mt8, fast.
**Hypothesis.** vs natural: WIN 4–6x (C(p+2,3) vs p³ cells; 5.9x at p=171 minus the
checksum tax). vs expert: parity to 1.3x loss (same cells; Blade's inner fold may
materialize a row temporary — blade-A verifies the emit).

### `antisym`
**Math.** X and Y are p x n (p particles, n components/samples). L(i,j) = Σ_s (X(i,s) Y(j,s)
− X(j,s) Y(i,s)) = (X Yᵀ − Y Xᵀ)(i,j), antisymmetric, zero diagonal. Named output: L.
Checksum: Σ_{i,j} L(i,j)² over the full logical matrix.
**Sizes.** smoke p=23, n=11. full p=1201, n=401 (strict packed C(1201,2) = 720,600 cells =
5.8 MB; dense 11.5 MB; inputs 7.7 MB; 1.16 GF strict, natural 2.3 GF; ≈ 0.3–0.6 s).
**Tiers.** natural: full p x p nest, both inner products per cell (or two `matmul`s and a
subtraction), dense. expert: strict upper triangle `j > i`, packed, jammed; may store the
sign-tracked half only.
**omp.** Blade: `lambda(a: T^2, b: T^2) where anticomm(a, b), omp(a: 1)` over
`method_for(Z, Z)` with Z = (p, 2, n) stacking X and Y → `[symmetric-storage v1] applied
[packed SymAntisymmetric ...]`, `[omp v1] ... DYNAMIC`. Fortran: `!$omp parallel do
schedule(dynamic)` on the outer loop of the strict triangle.
**Regimes.** serial, mt8, fast.
**Hypothesis.** vs natural: WIN ~2x (half the cells, none of the diagonal). vs expert:
parity ±15% (identical cell set; Blade pays the logical-read checksum tax ~2%).

### `pairkernel`
**Math.** n points in d=3 (coordinates x, y, z). K(i,j) = k_{5/2}(r_ij) with
r² = (x_i−x_j)² + (y_i−y_j)² + (z_i−z_j)², the Matérn-5/2 kernel k(r) = (1 + a + a²/3)
exp(−a), a = √5·r/ℓ, ℓ = 0.7. Symmetric, unit diagonal. Named output: K. Checksum:
Σ_{i,j} K(i,j)² over the full logical matrix.
**Sizes.** smoke n=53. full n=2503 (packed C(2504,2) = 3.13M cells = 25 MB; dense 50 MB;
≈ 3.1M sqrt+exp ≈ 0.1–0.2 s packed, 2x dense).
**Tiers.** natural: full n x n double nest, every cell evaluated. expert: `j >= i` triangle,
packed storage.
**omp.** Blade: `lambda(a: T^1, b: T^1) where comm(a, b), omp(a: 1)` over
`method_for(P, P)`, P = (n, 3). Fortran: `!$omp parallel do schedule(dynamic)` on the outer
loop.
**Regimes.** serial, mt8. (No fold inside the kernel: `fast` not applicable.)
**Hypothesis.** vs natural: WIN ~2x (half the transcendental evaluations). vs expert:
parity within 5% (transcendental-bound; language overhead invisible).

## Group B — language-level structure

### `fusedchain`
**Math.** a ∈ [0,1)ⁿ. s1 = 1.5a + 0.25; s2 = s1/(1+|s1|); s3 = s2² − 0.5 s2. Output (scalar):
checksum = Σ s3². No named array output: the stages are intermediates.
**Sizes.** smoke n=20011. full n=2,000,003 (a = 16 MB; natural's three temporaries 48 MB;
≈ 10–25 ms, memory-bound; input share ~35% → `inputonly` twin REQUIRED).
**Tiers.** natural: three array statements into three named arrays, then `sum(s3**2)`.
expert: one loop, scalar stages, one accumulator.
**omp.** Blade: a licence on the fold (spelling settled by blade-B; see README §7.4).
Fortran: `!$omp parallel do reduction(+:acc)` on the fused loop (expert) / on each array
statement + the sum (natural, via `!$omp workshare` or a loop).
**Regimes.** serial, mt8, fast.
**Hypothesis.** vs natural: WIN 1.5–2.5x (no temporaries: the fuse pass folds the chain
into the reduction; verified in a probe emit). vs expert: parity.

### `onepass`
**Math.** x ∈ [0,2)ⁿ. Four power sums S_k = Σ x^k, k = 1..4, in ONE traversal. checksum =
S1 + 2 S2 + 3 S3 + 4 S4. No array output.
**Sizes.** smoke n=50021. full n=5,000,011 (40 MB; ≈ 10–20 ms one pass; natural's four
passes ≈ 2–3x; input share ~50% → `inputonly` REQUIRED).
**Tiers.** natural: `sum(x)`, `sum(x**2)`, `sum(x**3)`, `sum(x**4)` (four passes). expert:
one loop, four accumulators.
**omp.** Blade: `<&!>` reduction join; a probe showed `omp(x: 1)` on a leg is emitted serial
("fold accumulates into a shared scalar") — blade-B must find the licensed spelling or
report "no parallel form". Fortran: `!$omp parallel do reduction(+:s1,s2,s3,s4)`.
**Regimes.** serial, mt8, fast.
**Hypothesis.** vs natural: WIN 2–3x (one pass over 40 MB vs four). vs expert: parity.

### `stencil`
**Math.** u is an ny x nx grid. lap(i,j) = u(i−1,j) + u(i+1,j) + u(i,j−1) + u(i,j+1)
− 4u(i,j) on the interior (ny−2) x (nx−2). Named output: lap. Checksum: Σ lap².
**Sizes.** smoke ny=31, nx=41. full ny=1501, nx=2003 (u 24 MB, lap 24 MB; ≈ 10–20 ms,
memory-bound; input share ~40% → `inputonly` REQUIRED).
**Tiers.** natural: one array-section statement `lap = u(1:ny-2, 2:nx-1) + ...` then
`sum(lap**2)`. expert: explicit nest, checksum fused into the same nest.
**omp.** Blade: `method_for(range<halo<Y,[-1,0,1]>, halo<X,[-1,0,1]>>) <@> lambda(wr, wc)
where omp(wr: 1) -> ...`. Fortran: `!$omp parallel do` on the row loop (`!$omp workshare`
for the natural array statement).
**Regimes.** serial, mt8.
**Hypothesis.** PARITY across all tiers (bandwidth-bound; no temporary in any tier). A
control: Blade is not expected to win; what it must show is no edge-guard overhead.

### `recur`
**Math.** nx independent initial values u0 ∈ [0,2). RK4 with h = 0.01 on du/dt = u − u³ for
nt−1 steps; the trajectory U(t, x), t = 0..nt−1, is the named output (U(0,·) = u0).
Checksum: Σ_{t,x} U(t,x)².
**Sizes.** smoke nt=11, nx=211. full nt=301, nx=20011 (48 MB trajectory; 6.0M cell-steps
x ~30 flops ≈ 60–150 ms; inputs negligible).
**Tiers.** natural = array statements per stage (`k1 = f(u)`, ...) inside a `do t` loop,
storing `u(:, t)`. expert: fused `do x` loop with scalar stages, no stage arrays.
**omp.** Not applicable (sequential in t; the x-parallelism inside a `let rec` step has no
licence slot today). Serial only.
**Regimes.** serial.
**Hypothesis.** vs natural: parity (same stage temporaries, L2-resident). vs expert: LOSS
1.5–3x. The parity control for the harness; also the drift-control kernel
(`recur/expert.f90`, gfortran, serial x repro; the stream control is `saxpy/expert.f90` at
n = 700,001).

### `maskagg`
**Math.** x ∈ [0,2)ⁿ. y = the subsequence of x with x_k > 1.0, order preserved (named
output). Checksum: Σ y² + count(y).
**Sizes.** smoke n=30011. full n=3,000,017 (x 24 MB, y ≈ 12 MB; ≈ 10–20 ms; input share
~45% → `inputonly` REQUIRED).
**Tiers.** natural: `y = pack(x, x > 1.0d0)`, `sum(y**2)`. expert: count pass + scatter
loop (or one pass with a running counter), `sum` fused.
**omp.** Not applicable (compaction is sequential in both languages; a parallel pack is a
different algorithm). Serial only.
**Regimes.** serial.
**Hypothesis.** LOSS 1.3–2x vs both tiers: Blade's `mask` writes a Bool array, `compound`
counts and gathers (three passes) vs pack's two. A control Blade is not expected to win.
**Known constraint.** `compound(dense, mask)` is only valid as a TOP-LEVEL let (BL6002
inside a function), and the LLVM lane prints every top-level binding (charter §3), so the
`blade-llvm` arm of this kernel is expected to be unmeasurable (array echo after the clock)
or refused; the `blade-cpp` arm (`--print checksum,input_checksum`) is the Blade figure.
blade-B records which.

## Controls

### `dot`
**Math.** a, b ∈ [0,1)ⁿ. checksum = Σ a_k b_k (the dot product itself).
**Sizes.** smoke n=30011. full n=3,000,017 (48 MB; kernel ≈ 3–6 ms; input ≈ 15–20 ms →
`inputonly` REQUIRED; the net figure is the result).
**Tiers.** natural: `dot_product(a, b)`. expert: a loop with one accumulator (strict forbids
more; `fast` allows a 4–8 way split).
**omp.** Blade: `reduce(method_for(zip(a, b)) <@> lambda(x, y) where omp(x: 1) -> x * y, (+))`
(the licence slot that streams — `prodsum(a, b)` has none; `reduce(a * b, lambda ... where
omp -> ...)` materializes the product; blade-B confirms which emits a pragma). Fortran:
`!$omp parallel do reduction(+:s)`.
**Regimes.** serial, mt8, fast.
**Hypothesis.** PARITY in every regime (one serial FMA chain in strict in all languages;
lanes in fast). Not expected to win.

### `saxpy`
**Math.** x, y ∈ [0,1)ⁿ, α = 1.5. y2 = αx + y (named output). Checksum: Σ y2².
**Sizes.** smoke n=20011. full n=2,000,003 (three 16 MB arrays; ≈ 4–8 ms; input ≈ 12 ms →
`inputonly` REQUIRED).
**Tiers.** natural: `y2 = alpha * x + y`, `sum(y2**2)`. expert: one loop with the store and
the accumulator.
**omp.** Blade: a licence on the map (`method_for(zip(x, y)) <@> lambda(u, v) where
omp(u: 1) -> alpha * u + v |> compute`) if the lifted spelling `alpha * x + y` has no licence
slot — blade-B decides; the lifted form MUST be forced with `|> compute` (a probe showed the
unforced chain fuses into the checksum and never stores y2). Fortran: `!$omp parallel do`.
**Regimes.** serial, mt8.
**Hypothesis.** PARITY (pure bandwidth). Not expected to win.

## Summary table

| kernel | group | full size | est. serial | regimes (fp x threads) | hypothesis (vs dense baseline / vs expert), licensed unless stated |
|---|---|---|---|---|---|
| gram | A | p=1601, n=1001 | 0.4–1.0 s | licensed, repro (serial, mt8), fastmath, blas | loss or equivalent / licensed: equivalence to modest loss (4–16 lanes vs i-vectorized outer product); repro: LOSS 3–8x (single chain); blas: equivalent |
| comoment3 | A | p=171, n=201 | 0.15–0.4 s | licensed, repro (serial, mt8), fastmath | WIN 4–6x / licensed: equivalence to modest loss; repro: equivalent to 2x loss |
| antisym | A | p=1201, n=401 | 0.3–0.6 s | licensed, repro (serial, mt8), fastmath | WIN ~2x / licensed: equivalence to modest loss; repro: equivalent to loss |
| pairkernel | A | n=2503 | 0.1–0.2 s | licensed, repro (serial, mt8; differ in the checksum fold only) | WIN ~2x / equivalent |
| fusedchain | B | n=2,000,003 | 10–25 ms | licensed, repro (serial, mt8), fastmath | WIN 1.5–2.5x / repro: equivalent; licensed: blade-llvm equivalent, blade-cpp loss (F11) |
| onepass | B | n=5,000,011 | 10–20 ms | licensed, repro (serial; mt8 Blade "no parallel form"), fastmath | WIN 2–3x / repro: equivalent; licensed: blade-llvm equivalent, blade-cpp loss (join legs reassociate in LLVM only) |
| stencil | B | 1501 x 2003 | 10–20 ms | licensed, repro (serial, mt8; differ in the checksum fold) | equivalent / equivalent |
| recur | B | nt=301, nx=20011 | 60–150 ms | licensed, repro (serial; mt8 Blade "no parallel form"; differ in the checksum fold) | equivalent / LOSS 1.5–3x |
| maskagg | B | n=1,000,003 | 3–10 ms | licensed, repro (serial only, by protocol; differ in both checksum sums, 8-lane in licensed) | LOSS / LOSS (2–5x) |
| dot | C | n=3,000,017 | 3–6 ms incremental | licensed, repro (serial, mt8), fastmath | repro: equivalent / equivalent; licensed: blade-llvm equivalent, blade-cpp LOSS 3–5x (F11) |
| saxpy | C | n=2,000,003 | 4–8 ms incremental | licensed, repro (serial, mt8) | equivalent / equivalent (licensed: blade-cpp detectable loss at most) |

`inputonly` twin required: fusedchain, onepass, stencil, maskagg, dot, saxpy.

Methodology-review corrections (README rev. after `notes/review-method.md`): "natural" is
the DENSE BASELINE (literal transcription, intrinsics, dense storage; gram keeps `matmul`),
"expert" the REVIEWED implementation; verdicts use δ = 10% (win / detectable / equivalent /
loss / inconclusive — "parity" is retired); the complete timed region is the result and the
twin quantity is the "incremental" estimate, never kernel time; the oracle is
`validation/pins.json`; `recur` gains Fortran mt8 arms (Blade "no parallel form");
`maskagg` is serial by protocol with full n = 1,000,003; two drift controls (`recur`
compute, `saxpy` stream at n = 700,001); `BLADE_FP_CONTRACT=fast` on every LLVM arm;
stencil/recur checksums are the per-row `prodsum` under a licensed fold. Names and the
other sizes unchanged. Later still (D40/D41): the fp regimes were renamed
`licensed`/`repro`/`fastmath` and the generator became the five-round variant D
(`h4 = ((h3*h3) div 65536) mod 2^31; h5 = (h4*1103515245 + 12345) mod 2^31; u = h5/2^31`;
reference h5 at seed 10000019: 408748897, 189020261, 1945512958 for k = 0, 1, 2).

Earlier corrections folded in (see `README.md` and `notes/design-decisions.md` D23–D26): the
LLVM lane has no OpenMP, so every mt8 Blade figure is `blade-cpp`; `maskagg` is
`blade-cpp`-only (LLVM refuses `compound`); `onepass` has no parallel Blade form;
`fusedchain` uses the composed `object_for >>@` chain (lifted lets materialize per let);
the input LCG is the corrected three-round generator with seeds `10000019 * j`; all ranges
are `[0,1)` or `[0,2)` (power-of-two width). Names and sizes unchanged.
