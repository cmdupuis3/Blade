# Kernel list (final) — Blade vs gfortran / ifx

Architect's deliverable 1. Directory names are FINAL; do not rename. Each kernel lives in
`bench/kernels/<name>/` with `spec.md` (architect), `<name>.blade` (blade-A / blade-B),
`natural.f90` + `expert.f90` (fortran), `kernel.json` (harness). The per-kernel `spec.md`
is authoritative where this page and the spec disagree.

Conventions used below: `p`, `n`, ... are the SIZE parameters (see `bench/kernels/README.md` §2 for the
`SIZE` marker). "cells" = output cells. Flop and time estimates are back-of-envelope
(never reported as results). All working sets include the natural tier's dense arrays and
stay <= 64 MB. No extent is a power of two.

Regimes (fp axis per README §3 / D40: `licensed` headline, `repro` secondary, `fastmath`
optional — "strict"/"fast" below are the retired names, read strict → repro and fast →
fastmath): every kernel runs `licensed x serial x native` and `repro x serial x native`.
`mt8` applies to every kernel's Fortran arms except `maskagg` (serial by protocol); Blade
reads "no parallel form" for `recur` and `onepass`. `fastmath` applies to the fold-bearing
kernels marked `fast` below. `blas` applies to `gram` only. Licensed-regime hypotheses (corrected by blade-A F15, Fortran review F2, and the compiler
flip 5f4b33ea = D43): `licensed` is now Blade's DEFAULT (no knob), `repro` is
`BLADE_FP_REPRO=1`; `licensed` differs from `repro` for ALL ELEVEN kernels (every
input-checksum fold reassociates by default; maskagg's output checksum too — its
compaction alone stays sequential); the expert's matched `simd reduction` set includes
every kernel's input-checksum folds (README §3 table). Since the flip BOTH Blade lanes
vectorize the deferred folds of dot, fusedchain, onepass and saxpy, so the earlier
"blade-cpp stays serial (F11)" rows below are history: licensed hypotheses vs expert for
those four are "both Blade lanes equivalent". The inner `prodsum` of gram/comoment3/antisym has no
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

- **A5 `band` — dropped on 2026-10-17, RESTORED on 2026-10-18 after G4 was fixed** (agent
  C, 1ea0ddf3 / 4b1a5224: linear-conjunct domains stream as an affine nest in all lanes).
  Spec: `kernels/band/spec.md` — the banded quadratic form xᵀBx over `range<Band>` with
  `abs(i - j) <= w`, n = 20011, w = 101 (4.05M band cells vs 4e8 dense visits), hypothesis
  equivalent to the expert's clipped-bounds nest, ~50–100x over the predicate-nest
  baseline. Unmeasured as of the 2026-10-18 matrix; sources to be written by blade-B and
  the Fortran agent after the rebuild. The original reason for dropping it follows.
- **A5 `band` was DROPPED (2026-10-17).** `reduce(method_for(range<Band>) <@> k, (+))` is refused at
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

(D44, 2026-10-18: in mt8 every group-A Blade source threads its input map and checksum
nest as well as the kernel's level 0 — the `**omp.**` lines below describe the kernel
licence only; the Fortran arms thread generation, kernel and checksum alike.)

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
**omp.** Blade: a licence on the fold (spelling settled by blade-B; see kernels/README.md §4.4).
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

## Round 3 additions (2026-10-20, W1-5; catalog N07 and N09)

Added from `round3/research/C-workloads.md` with the Codex review C's corrections; each
spec (`kernels/<name>/spec.md`) is authoritative. Both are unmeasured until the
orchestrator's A/B; `run.py check --regimes all` is clean at smoke size for every arm.

### `twofir`
**Math.** x ∈ [0,1)ⁿ. y1 = the 7-tap FIR (1, 2, 3, 4, 3, 2, 1)/16 over centres 3..n−4;
y2 = 0.5 x(i) − 0.25 x(i∓4) + 0.125 x(i∓8) over centres 8..n−9. Named outputs: y1, y2
(different domains). Checksum: s1 + s2 (separate +0 chains Σ y1², Σ y2²). Honestly scoped
as **resident two-output FIR / sibling-map fusion** (review C/13), not ghost reuse: the
resident `Chunked` axis of the catalog is invisible to a halo walk, so the source uses a
plain `Idx<n>`.
**Sizes.** smoke n=20011; full n=2,000,003 (x, y1, y2 = 3 x 16.0 MB = 48.0 MB with the live
input included; input share large → `inputonly` twin per arm).
**Tiers.** natural: two loops, one per output, then one sum per output. expert: one fused
loop over y2's domain writing both outputs from the same loads, y1's five head and five
tail centres peeled, both sums fused.
**omp.** Blade: `where omp` on the generator, both halo maps, s1, s2 and the input sum
(`axes = 1`); Fortran: `parallel do` on every loop (expert: on the fused loop).
**Regimes.** licensed, repro (serial, mt8).
**Hypothesis.** equivalent / LOSS ~0.70–0.85 today (two FIR passes + two checksum passes
vs one fused pass); equivalent once the sibling maps fuse.

### `tensorop`
**Math.** Y = (A ⊗ B ⊗ C) X as three ordered mode contractions (I, then J, then K) over
X (n1, n2, n3). Required outputs X, A, B, C, Y only (review C/1); T1, T2 are intermediates;
the factor order is fixed, per-cell chains +0-seeded ascending. Checksum: Σ Y².
**Sizes.** smoke 11/13/17; full 89/97/101 (each tensor 6.98 MB; 250M FMAs; Blade peak
≈ 35 MB with mode_product's work buffer, Fortran 27.9 MB); `inputonly` twins provisioned
(input share estimated 5–15% of the expert's region).
**Tiers.** natural: three ordered textbook nests (output cells row-major, contraction
innermost) — a recorded deviation from the catalog's slice-`matmul` natural, which the
Fortran tier review assesses against the ascending-chain requirement before publication.
expert: three GEMM-shaped 8 x 6 register-tiled contractions on views of flat storage, same
factor order and per-cell chains.
**omp.** Blade = the library census baseline, the existing `m.mode_product` chain: a
synthesized serial nest with no licence slot (serial kernel in every regime; generators and
checksums licensed: checksums threaded in licensed / fastmath, serial in repro); the
licensed map spelling (stronger, different permissions) is recorded for the later
recognizer comparison.
Fortran: `parallel do` on every nest.
**Regimes.** licensed, repro (serial, mt8), fastmath.
**Hypothesis.** equivalent (serial) / LOSS 3–10x (strided single-chain nests vs register
tiles); the census target for a later contraction recognizer.

## Round 3 wave-2 addition (2026-10-21, W2-5; catalog N02, the fallback for N10)

N10 `groupls` (grouped Gauss-Newton local models) was the item; its emission gate failed —
a row map whose cell is a per-group packed `comm` H does not run in any lane (C++ BL8005
`std::bad_array_new_length`, LLVM refusal + fallback, interpreter BL8003; gap register
G67; reproducers `notes/repro/W2-5-G67-*.blade`) — so the corrected N02 landed instead.
Unmeasured; `run.py check --regimes all` clean at smoke size for every arm.

### `haloadjoint`
**Math.** x, w ∈ [0,1)ⁿ. F_c(x) = x(c−1) x(c+1) + sin(x(c)) x(c+2) − (0.5 x(c−2)) x(c−2)
over centres 2..n−3. Required outputs: y = F(x) (n − 4 cells) and dx = Jᵀw (n cells) from
`ad.grad` of L = Σ w(c) F_c. The derivative reference is an EXPLICIT ORDERED UPDATE LIST
(review C/2): +0, then per destination U1 (offset +2), U2 (+1), U3 (0), U4 (−1), U5 / U6
(the two −2 occurrences, left then right), each present only when its window exists —
the emitted sweep's order, checked by exact numeric equality (`==`) against both Blade lanes
and the interpreter (`dump/haloadjoint_order.blade`) and implemented by both twins and the
oracle (gfortran twins raw-bit equal to it with contraction off). Checksum: Σ y² + Σ dx².
Blade also computes AD-required auxiliaries its source names (the forced loss tape d, the dw
buffer, the loss value lv) and a synthesized cotangent of d — disclosed, stored, not
required, and not deletable under this source.
**Sizes.** smoke n=1013; full n=1,000,003, the catalog's (round 3 wave 4, W4-4: until then
capped at n=50,021 by the compiler -- G68, the reverse sweep's cotangent zero buffer was an
(n − 4)-element literal whose lowering was super-linear in n; FIXED, the buffer is a zero
fill and the front end is flat in n, 2.8 s for `blade emit` at full size). The required
arrays are 32.0 MB and the Blade arm's 56.0 MB (arithmetic upper bound, every array resident
at once; no literal image any more); the W4-4 check's single runs saw Blade peak working sets
of 58.1-59.0 MB. NEITHER is the per-arm peak-live / store audit the pre-fix G68 row required:
that audit and the machine-code store verification of y and dx are PENDING for the
integration measurement gate (spec.md "Memory audit status"), and the LLVM lane's tape and
cotangent escape the gradient function's cleanup (G78, pre-existing, OPEN). `inputonly` twin
per arm (input share est. 10–30%).
**Tiers.** natural: the primal loop, dx = 0, then one destination-indexed loop per update in
list order. expert: one fused destination-owned pass (y, dx, both sums), approximately one
sin and one cos per cell from per-block strips (sin n + 2B, cos n − 4), 8 boundary cells
peeled.
**omp.** Blade: `where omp` on the generators, the primal map, the buffer fills, both
checksum folds and the input sums; the loss map and the adjoint passes cannot carry a licence
(BL5500) and are serial. Fortran: `parallel do` on every loop (expert: over blocks).
**Regimes.** licensed, repro (serial, mt8).
**Hypothesis.** LOSS ~0.5–0.8 / LOSS ~0.35–0.6 today (transcendental calls 5(n − 4) in this
emission — the extra sin calls re-evaluate arguments another pass already evaluated — vs
3(n − 4) vs approximately 2n; 7 serial destination passes vs 1 fused pass); closing it needs
the per-update passes merged into one destination pass and cross-pass sharing of the primal,
with d and dw still stored.

## Round 3 wave-3 addition (2026-10-21, W3-2; research B, item T2)

### `hashjoin`
**Math.** Two keyed tables (lk, la) over N rows and (rk, ra) over M rows, integer keys
uniform over K values and integer amounts in [0, 1024). Named output: the relation
`matched = (la(i) ra(j) : lk(i) == rk(j))` in LEX order (i ascending, then j) — a BAG,
duplicates on both sides kept. Checksum: total + J + fp, fp the order fingerprint
(3 a + b) mod 1000000007 from 0 (exact, non-overflowing: review B/13).
**Sizes.** PILOT ONLY: smoke N 211 / M 101 / K 41 (J = 522); full = the research pilot
N 2003 / M 1009 / K 401 (P = 2,021,027 pairs, J = 5000). The research full size
(N 100,003, M 20,011) is not benched this round. `inputonly` twin per arm.
**Tiers.** natural: two N x M nested loops (count, then append in lex order). expert: a
counting-sort CSR over [0, K), a counting pass sizing J, the probe storing `matched` with
total and fp fused. Blade: the INLINE spelling (the outer products compiler temporaries),
which the C++ lane's hash join (G72) turns into the stable CSR + probe; the named spelling
keeps every named array stored and is not this kernel.
**omp.** None: serial in every arm (an output-owned threaded probe is legal but unbuilt).
**Regimes.** licensed, repro (serial; integer data, the same arithmetic).
**Hypothesis.** WIN ~10–20x / LOSS ~0.75–0.9 (the consumer folds stay separate passes;
the expert fuses them into the probe). Directional (W3-2, PROVISIONAL: gfortran only, ifx
neither built nor store-audited): expert/NEW 0.84 on the TOTAL region (the DROP criterion,
> 0.8), 0.75 input-net (reported, not the criterion), natural/NEW 15x, OLD/NEW 108x.
Complete-kernel cost NEW 22,434 operations / 414 KB source-logical traffic vs the expert's
21,826 / 230 KB (construction alone 12,434 / 334 KB vs 11,826 / 230 KB; spec.md). Store
audit: blade-cpp and both gfortran tiers keep every input and `matched` store; OLD is not
audited, so OLD/NEW 108x is not an audited ratio. Timings are bc3ddac9's emission; the
overflow guards are not re-timed.

## Summary table

| kernel | group | full size | est. serial | regimes (fp x threads) | hypothesis (vs dense baseline / vs expert), licensed unless stated |
|---|---|---|---|---|---|
| gram | A | p=1601, n=1001 | 0.4–1.0 s | licensed, repro (serial, mt8), fastmath, blas | loss or equivalent / licensed: equivalence to modest loss (4–16 lanes vs i-vectorized outer product); repro: LOSS 3–8x (single chain); blas: equivalent |
| comoment3 | A | p=171, n=201 | 0.15–0.4 s | licensed, repro (serial, mt8), fastmath | WIN 4–6x / licensed: equivalence to modest loss; repro: equivalent to 2x loss |
| antisym | A | p=1201, n=401 | 0.3–0.6 s | licensed, repro (serial, mt8), fastmath | WIN ~2x / licensed: equivalence to modest loss; repro: equivalent to loss |
| pairkernel | A | n=2503 | 0.1–0.2 s | licensed, repro (serial, mt8; differ in the checksum fold only) | WIN ~2x / equivalent |
| fusedchain | B | n=2,000,003 | 10–25 ms | licensed, repro (serial, mt8), fastmath | WIN 1.5–2.5x / equivalent in both regimes (both Blade lanes vectorize the fold since the flip) |
| onepass | B | n=5,000,011 | 10–20 ms | licensed, repro (serial; mt8 Blade "no parallel form"), fastmath | WIN 2–3x / equivalent in both regimes (both lanes reorder the four legs since the flip) |
| stencil | B | 1501 x 2003 | 10–20 ms | licensed, repro (serial, mt8; differ in the checksum fold) | equivalent / equivalent |
| recur | B | nt=301, nx=20011 | 60–150 ms | licensed, repro (serial; mt8 Blade "no parallel form"; differ in the checksum fold) | equivalent / LOSS 1.5–3x |
| maskagg | B | n=1,000,003 | 3–10 ms | licensed, repro (serial only, by protocol; differ in both checksum sums, 8-lane in licensed) | LOSS / LOSS (2–5x) |
| dot | C | n=3,000,017 | 3–6 ms incremental | licensed, repro (serial, mt8), fastmath | equivalent / equivalent in both regimes (both Blade lanes vectorize the zip fold since the flip; natural's `dot_product` loses to expert in licensed) |
| saxpy | C | n=2,000,003 | 4–8 ms incremental | licensed, repro (serial, mt8) | equivalent / equivalent |
| twofir (round 3) | B | n=2,000,003 | 10–25 ms | licensed, repro (serial, mt8) | equivalent / LOSS ~0.70–0.85 (two passes vs one fused pass) |
| tensorop (round 3) | A | 89 x 97 x 101 | Blade 0.2–0.6 s, expert 20–60 ms | licensed, repro (serial, mt8), fastmath | equivalent (serial) / LOSS 3–10x (mode_product nests vs register tiles; Blade kernel serial in mt8) |
| haloadjoint (round 3) | B | n=1,000,003 (raised from 50,021 by W4-4, G68 FIXED) | 2–8 ms at the former n; not yet timed at this n | licensed, repro (serial, mt8; the adjoint serial in mt8) | LOSS ~0.5–0.8 / LOSS ~0.35–0.6 (per-update passes + source-named AD auxiliaries vs one fused pass) |
| hashjoin (round 3, pilot) | B | N=2003, M=1009, K=401 | ~0.1 ms | licensed, repro (serial only) | WIN ~10–20x / LOSS ~0.75–0.9 (separate consumer folds vs folds fused into the probe) |

`inputonly` twin required: fusedchain, onepass, stencil, maskagg, dot, saxpy, twofir, haloadjoint, hashjoin;
provisioned (keep if the pilot measures > 5%): tensorop.

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
