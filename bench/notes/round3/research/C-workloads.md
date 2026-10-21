# Round 3, item C: the N01-N10 workloads (research brief)

Scope: for the catalog's first ten workloads (`new-kernel-catalog.md`): Blade-expressible today?, the Fortran twins, the
bench contract, the transformation exercised, and the order to add them.

Method: source and doc reading, plus exactly ten `blade check` calls (one per workload, smoke size, sequential, private
cwd, bench-fortran binary e64fef3b, Blade.dll SHA-256 `C7B14497...`, ucrt64 first on PATH); no plan, emit, run or g++.
The complete checked files are
`C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\c-work\n01.blade`
.. `n10.blade`. Each carries the suite's variant-D `gen` verbatim (from `bench/kernels/recur/recur.blade`) and the `let
(checksum, input_checksum) = { ... }` contract. Below, each workload shows only its kernel core.

**Result: all ten return `OK` (exit 0).** `check` covers typecheck, lowering and IR validation. It does not run the C++
or LLVM back ends, so BL7xxx refusals and BL9002 are not excluded. The workloads with a real codegen risk are N05, N10,
N03, N08 and N02 (named in each section). Every ratio below is a **hypothesis for a new kernel**. None is measured.

## 1. Hook verification (catalog claim -> current location -> status)

| catalog claim | current location (bench-fortran e64fef3b) | status |
|---|---|---|
| halo access descriptions and a backward gather route exist (02) | `src/GradNormalize.fs:haloGatherPlan` 118-165 (route G: sole literal halo, a rank-0 inline lambda, reads `x(w(k))`); knob `GradCommon.fs:304` `BLADE_AD_HALO_GATHER`; emission 02 §3.3.1 is one `for __j` loop **per offset**, descending o, each its own `+=` | holds. The parallel gather (02 §3.3.3 "P2") is NOT built, so it is serial |
| array VJP is available | `ad.vjp` exists (corpus ad-jvp-comb/110, 113), but `GradNormalize.fs:93,197,699` and `GradExpand.fs:1487` gate the map and halo lowering on `errMode = "grad"` | changed: a map in a vjp body is probably refused (inferred, no corpus). N02 therefore uses `ad.grad` of the weighted loss `sum w*F` |
| multidimensional halo AD is C | `GradNormalize.fs:302` refuses a multi-index `range<I, J>` in reverse mode; the nested `halo<G,[[..],[..]]>` is refused (:336); the scatter route caps at 65536 cells (`:116 maxLoweredCells`) | holds, and it is a language gap: N02 is 1-D only |
| first-order recurrence reverse mode exists; bounded lag incomplete (01) | `GradExpand.fs:1266`: only rank-1 (scalar-slice) `let rec` is differentiable, and lag >= 2 is refused (:1244) | holds, narrower than the catalog says: vector-state recurrences are not differentiable in either mode, so N08's tangent is hand-written |
| factor values + JVP/VJP through solves | `src/math/compiler/MathElaborate.fs:9-13` (`m.lu`, `m.lu_solve`, `m.lu_solve_t`); corpus ad-jvp-comb/113; `IRLuSolve` is an inline form the lift pass binds to a let (`src/IRLift.fs:25,74`) | holds. A per-kernel-cell solve (N05) checks, but codegen is unverified |
| `*_at` RNG is pure and addressable | `src/rand/compiler/RandElaborate.fs:126-133`; in-function and in-kernel draws in corpus rand/031, 033; result index anonymous (`TypeCheckInfer.fs:1064-1071`) | holds. A draw inside a `let rec` arm is not pinned in the corpus, but N08 checks |
| segmented halo reads exist (07) | the run-plus-ghost path `segmentRun` is `src/CodeGenCuda.fs:3456-3478`, and only for a STREAMED source (`ZarrTests.fs` 10g); an in-memory `halo<CI,..>` over `Chunked<I,K>` is a flat halo | changed: for resident data the chunk edge is invisible, and N07's "ghost reuse" only exists file-backed |
| rolling recurrence storage is C | `docs/plans/plan-rec-array-windowing.md:3`: built on the unmerged `claude/recursive-array-materialization-381708`; a port is a re-implementation | holds. It is the prerequisite for N01's meaningful size and for N04's/N08's terminal-only contracts |
| endpoint reduction over compact pairs is C/P | `docs/plans/plan-compact-sym-folds.md:10`: partial folds over a compact group keep BL3999; W2-A ordered scatter consumes a STORED SymIdx pool (RESULTS 15.2) | holds. N06 reads a deferred `comm` producer through `w(i, j)` (the orbit sketch's checked spelling) |
| tensor contraction needs an operator design (P) | `m.mode_product` exists (`MathElaborate.fs:349-367`, `MathDecls.fs:649` serial synthesized nest); `m.matmul` routes to the native packed `blade_pgemm::dgemm_nn` (`CodeGenExpr.fs:3787`, rule `CodeGenExprSupport.fs:2065 packedGemmWorth`) | changed: more exists than the catalog states (A, not P, for an explicit composition) |
| recur-style step threading (G5) | wave 3 E-G5 (magical-cannon b4a21003, NOT in the bench-fortran binary): admits a step only when every trajectory read is `prefix(n - c)` **at the cell being written** (RESULTS 16.1) | N01's stencil step and N08's coupled (S, D) state are outside that admission |

## 2. Common contract

- Inputs come from `gen` (variant D, seeds `10000019 * j`, power-of-two ranges) and are stored; arrays live in
  functions, only `checksum` / `input_checksum` at top level (N02 excepted, see there); no power-of-two extent. The 64
  MB rule binds N01, N04, N08, N10 today (a `let rec` stores its whole trajectory; named lets are stored). Each
  workload's own fold is a builtin `(+)` (licensed by source) or a non-builtin kernel (serial everywhere). Every
  proposed pass admits through `Optimize.scheduleMayWrite` for producer, consumer, outer fold kernel, seed and
  intervening bindings (constraint 4); no source writes a capture.

## 3. The workloads

### N01 stenciltt: terminal state of a nonlinear 2-D stencil (check: OK)

```blade
function step(u: Array<Float64 like Y, X>) -> Array<Float64 like Y, X> = {
    method_for(range<Y, X>) <@> lambda(i, j) where omp(i: 1) -> {
        let c = u(i, j)
        if Int64(i) == 0 || Int64(i) == ny - 1 || Int64(j) == 0 || Int64(j) == nx - 1 then c
        else c + 0.1 * (u(i - 1, j) + u(i + 1, j) + u(i, j - 1) + u(i, j + 1) - 4.0 * c) + 0.01 * (c - c * c * c)
    }
}
function kernel(u0: Array<Float64 like Y, X>) -> Float64 = {
    let rec traj: Array<Float64 like T, Y, X> =
        match traj with
        | zero -> zero
        | zero :: s -> zero :: u0
        | prefix :: n -> prefix :: step(prefix(n - 1))
    let ufinal = traj((nt - 1 : T)) |> compute
    reduce(method_for(ufinal) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p + q, 0.0)
}
```

`halo` has no boundary mode (formalism.md:359 `align ... Pad/Periodic` is planned), so a fixed-boundary full-extent step
must be a `range<Y, X>` map with a guarded `if`. Its `i - 1` reads are positions that carry a BL8006 run-time check.
**Natural Fortran:** two arrays, `do t` around the array statement `unew(2:ny-1,2:nx-1) = ...`, then a swap. mt8 adds
`!$omp workshare`. **Expert:** a two-buffer sweep with hoisted boundary rows, then an overlapped (trapezoid) time tile
of tau steps over row blocks (tau in {2, 4, 8}, block height chosen for L2), with redundant halo recompute at block
edges. mt8 adds `parallel do` over blocks inside each slab. **Contract.** smoke 31 x 41 x 7. The full size is
cap-limited TODAY to 211 x 307 x 101 (trajectory 52.3 MB). The tiled size, which needs windowing first, is 1501 x 2003 x
21 (two 24 MB planes). `checksum = sum ufinal^2`, `input_checksum = sum u0`. Regimes serial/mt8 x licensed/repro (only
the checksum fold differs). An inputonly twin applies only at the tiled size (generation is about 5% there).
**Exercises.** The prerequisites come first: windowing (demand is the last row only) and destination passing for a
NON-pointwise step (slice-write fusion today declines a stencil step, which builds a slice temp and copies it). Then
E-G5 generalized to a neighbourhood read of the previous row (barrier per step, or a tau-step ring), then time tiling
(catalog C, an R-legal schedule because each cell's expression tree is unchanged). Item A: stencil mt8 is a remaining
loss (15.3: 0.822 / 0.875 repro), and G49 (the teardown inside the timer) grows with the trajectory. **Hypothesis.**
Today (cap size) a **LOSS of about 2-4x**: a 52 MB trajectory write plus first-touch and teardown, a per-step slice temp
and copy, and checked reads, against the expert's two 518 KB L2-resident buffers. With windowing and destination passing
it should reach **parity** with the two-buffer expert. Time tiling only pays above L3 (tiled size), and against a
time-tiled expert parity is the ceiling. **Effort M**: Blade S, Fortran tile M, windowing port L.

### N02 halovjp: nonlinear halo primal plus J^T w (check: OK, 1-D)

```blade
// primal(x): the same halo map without the wt(q(0)) factor, bound to y, |> compute, returned
function loss(x: Array<Float64 like I>, wt: Array<Float64 like I>) -> Float64 = {
    let d = method_for(halo<I, [-2, -1, 0, 1, 2]>) <@> lambda(q) ->
        wt(q(0)) * (x(q(-1)) * x(q(1)) + sin(x(q(0))) * x(q(2)) - 0.5 * x(q(-2)) * x(q(-2))) |> compute
    reduce(d, (+))
}
let x = field(10000019)
let wt = field(20000038)
let mut dx: Array<Float64 like I> = method_for(range<I>) <@> lambda(i) -> 0.0 |> compute
let mut dw: Array<Float64 like I> = method_for(range<I>) <@> lambda(i) -> 0.0 |> compute
let lv = ad.grad(loss)(x, wt, dx, dw)
let y = primal(x)
let checksum = prodsum(y, y) + prodsum(dx, dx)
```

The cotangent w is read at each window's centre, so `dx = J^T w` and `dw = F(x)` on the interior. The arrays are at top
level (a corpus-proven shape). The bench needs them inside a function, because the LLVM lane prints every top-level
binding. Whether `ad.grad` with local `let mut` buffers works inside a function is the first probe. **The 2-D version
does not check: this is a language gap** (reverse mode lowers only single-index loops, `GradNormalize.fs:302`).
**Natural Fortran:** a primal loop plus the reverse loop written as a scatter, `dx(i+o) += w(i) * dF_o(i)`. **Expert:**
one fused destination-owned loop over j: compute y(j), keep sin/cos of a 5-cell register ring, and gather the 5
contributions into dx(j) in descending-o order, each its own `+=` (bitwise equal to route G, 02 §3.3.4). mt8: `parallel
do` over j blocks. **Contract.** smoke n = 1013; full n = 1,000,003 (x, w, y, dx, dw, d: 48 MB). `checksum = sum y^2 +
sum dx^2`, `input = sum x + sum w`. Regimes serial/mt8 x licensed/repro. Inputonly twin: yes. In mt8 the Blade adjoint
is "parallel preparation, serial kernel" until P2 lands. **Exercises.** This is the access-description track (structural
02), not A/B/D: (1) merge route G's 5 per-offset passes into one destination pass (same per-cell order, so R); (2) share
the primal between `y` and the loss map's stored `d`; (3) the P2 independent-iteration mark for threads
(`CodeGenBinding.genForRangeBinding`). Round-2 relation: a cell epilogue (W1-B) fuses the checksum at the stores.
**Hypothesis.** Today a **LOSS of about 3-5x**: about 7 passes over n (the y map, the d map, 5 gather passes with RMW of
dx) against 1, and roughly 4 transcendental evaluations per cell against 2. Parity after the merge. **Effort M**: the
oracle needs the analytic adjoint; the expert's hand-derived adjoint needs review.

### N03 raggedattn: ragged causal attention, a specified online state (check: OK)

```blade
let o = method_for(range<Q>) <@> lambda(i) where omp(i: 1) -> {
    let qi = qf(i)
    let lo = st(i)
    let cnt = Int64(i) - lo + 1
    let s = reduce(method_for(0..cnt) <@> lambda(jj) -> {
            let j = (lo + jj : Q)
            (prodsum(qi, kf(j)) * 0.25, 1.0, v(j, (0 : V)), v(j, (1 : V)), v(j, (2 : V)), v(j, (3 : V)))
        },
        lambda(a, b) -> {
            let m = if a[0] > b[0] then a[0] else b[0]
            let ca = exp(a[0] - m)
            let cb = exp(b[0] - m)
            (m, a[1] * ca + b[1] * cb, a[2] * ca + b[2] * cb, a[3] * ca + b[3] * cb, a[4] * ca + b[4] * cb, a[5] * ca + b[5] * cb)
        }, (-1.0e300, 0.0, 0.0, 0.0, 0.0, 0.0))
    let row: Array<Float64 like F> = [s[0] + log(s[1]), s[2] / s[1], s[3] / s[1], s[4] / s[1], s[5] / s[1]]
    row
} |> compute
```

Segment starts `st` come from a `let rec` over the flat axis: a break when `gen(i) < 1/48`. This makes the topology an
input object whose creation is inside the timer. Ragged segmentation via `group_by` is **not** usable here: a per-member
result over a ragged group is refused (`CodeGenCuda.fs:2825-2833`). A `static struct` causal domain cannot take a
runtime bound. A vector-valued tuple slot is unproven, so d_v = 4 is spelled as four scalar slots. **Codegen risk:** a
runtime `0..cnt` inside a threaded kernel (it may allocate per query), and the LLVM lane refuses the tuple fold
(`expected_llvm: fallback`). **Natural Fortran:** per query, a two-pass stable softmax over its key interval (scores
into a scratch row). (A different algorithm: its own arm or none.) **Expert:** the identical online recurrence, with
queries blocked by 4 sharing each K/V load inside a segment, and keys in ascending order. **Contract.** smoke nq = 307;
full nq = 200,003 (P is about 4.9M allowed pairs; inputs about 34 MB). `checksum = sum O^2`, `input = sum Q + sum K +
sum V`. Regimes serial/mt8 x licensed/repro: the online fold is a non-builtin kernel, so it is serial in every regime
and the regimes differ only in the checksum. No twin. **Exercises.** Summary-aware lowering is catalog C/P. The new
machinery is lanes ACROSS queries, each lane keeping its own serial chain (R-legal, the "jam different cells" rule), and
an LLVM tuple-fold arm. Item B: segment-scoped pairs are the band-shaped "join within a segment". Round-2: the G7 join
team is the threaded analogue of the tuple state. **Hypothesis.** **Parity to a 1.5x LOSS** (blade-cpp; both sides are
exp-bound, about 2 exp per pair). A win is unlikely. **Effort M.**

### N04 banddp: banded edit-distance DP with a bounded live frontier (check: OK)

```blade
function cell(i: Int64, k: Int64, prev: Array<Int64 like Wk>, left: Int64,
              a: Array<Int64 like Nx>, b: Array<Int64 like Nx>) -> Int64 = {
    let j = i + k - w
    if j < 0 || j > n then inf
    else if j == 0 then i
    else {
        let up = if k + 1 < bw then prev((k + 1 : Wk)) + 1 else inf
        let lf = if k == 0 then inf else left + 1
        let sub = if a((i - 1 : Nx)) == b((j - 1 : Nx)) then 0 else 1
        imin(imin(up, lf), prev((k : Wk)) + sub)
    }
}
function band_row(i: Int64, prev: Array<Int64 like Wk>, a: Array<Int64 like Nx>, b: Array<Int64 like Nx>) -> Array<Int64 like Wk> = {
    let rec cur: Array<Int64 like Wk> =
        match cur with
        | zero -> zero
        | prefix :: k -> prefix :: cell(i, Int64(k), prev, prefix(k - 1), a, b)
    cur
}
// kernel: let rec dp: Array<Int64 like Rows, Wk> = ... | zero :: s -> zero :: row0()
//                                                   | prefix :: i -> prefix :: band_row(Int64(i), prefix(i - 1), a, b); last = dp((n : Rows)) |> compute
```

Rows are in band-local coordinates `k = j - i + w`, so an outer `let rec` over rows nests an inner one over k (formalism
7.5: "the slice expression may itself be a let rec"). The in-row dependence on `k - 1` rules out a map. The
implicit-zero read at k = 0 is guarded, and so is the `k + 1` overflow column. **Natural Fortran:** a full (n+1)^2
matrix with a band predicate, a straw man by construction (as for `band`). **Expert:** two rows of 2w+1 integers, the
loop bounds clipped to `[max(1,i-w), min(n,i+w)]`, a branch-free interior and no table. **Contract.** smoke n = 211, w =
5; full n = 20011, w = 101 (4.06M cells; Blade's table is 32.5 MB today); narrow w = 11. Symbols are `Int64(floor(gen *
4))`. `checksum = Float64(score + sum of the final row's valid cells)`, `input = sum a + sum b`. Regimes: serial
licensed plus serial repro (integer, so they are identical; repro serves as a control). No parallel form (one problem):
`no_parallel_form: true`, with a batched-B variant for mt8 later. No twin. **Exercises.** (1) windowing (rows `< i - 1`
are dead); (2) destination passing of a nested `let rec` slice straight into the outer row (today it is an inner buffer
per row plus a copy); (3) closed-form band bounds instead of per-cell `j < 0 || j > n` tests and checked casts (the
`domain-enum` bounds of `band` reused for a recurrence). Item A: the recur-class seams (G6/G8 allocator and copy
history). `min`/`max` are static-only (`docs/features.md:126`), hence the hand-written `imin`. **Hypothesis.** Today a
**LOSS of about 2-5x** (a 32.5 MB table write, a per-row inner buffer and copy, branches, and 3 checked casts per cell,
against an L1-resident two-row loop). Parity once (1)-(3) land. **Effort M.**

### N05 lufactor: factor-once ensemble primal and tangent solves (check: OK; codegen unverified)

```blade
function system(a: Array<Float64 like Q, Q>, e: Array<Float64 like Q, Q>,
                b: Array<Float64 like R, Q>, db: Array<Float64 like R, Q>) -> Array<Float64 like Two, R, Q> = {
    let f = m.lu(a)
    let x = method_for(range<R>) <@> lambda(k) -> m.lu_solve(f, (b(k) : Array<Float64 like Q>)) |> compute
    let rr = method_for(range<R, Q>) <@> lambda(k, i) -> db(k, i) - prodsum(e(i), x(k)) |> compute
    let dx = method_for(range<R>) <@> lambda(k) -> m.lu_solve(f, (rr(k) : Array<Float64 like Q>)) |> compute
    let out = method_for(range<Two, R, Q>) <@> lambda(c, k, i) -> if Int64(c) == 0 then x(k, i) else dx(k, i) |> compute
    out
}
// kernel: let xd = method_for(range<G>) <@> lambda(g) where omp(g: 1) -> system(a(g), e(g), b(g), db(g)) |> compute
```

The factor is one value per system, and X and dX are stacked in one stored output `XD(g, 2, r, q)`. That is forced: an
array of factor tuples is not expressible, and two separate output maps would factor twice. `m.lu(A(g))` directly is
refused (BL5200: no shape witness), so each system goes through an annotated function. `A_g = U + 7 * Pi_g`, with
`Pi_g(i) = (3i + g) mod 7`, gives deterministic row swaps at a bounded condition number. **Codegen risk:** `m.lu_solve`
per kernel cell is lifted to a let (`IRLift.isInlineForm`), with one fresh q-pool per solve (about 10 allocations per
system). **Natural Fortran:** per system, a call to a hand-written `getrf`-style LU, then `getrs`-style solves per RHS.
**Expert:** factor in place (q = 7, held in registers or L1), solve all 2r RHS against the same factor in one triangular
sweep, and vectorize across systems with an AoSoA [g-block][q][q] layout only if it pays (the old prototype preferred
gather). mt8: `parallel do` over g. **Contract.** smoke ng = 29; full ng = 20,011 (q = 7, r = 5; 38 MB; about 17 MFLOP,
so a short kernel). `checksum = sum XD^2`, `input = sum A + sum E + sum B + sum dB`. Regimes serial/mt8 x licensed/repro
(the `prodsum(e(i), x(k))` dot is licensed; the LU loops are serial everywhere). Inputonly twin: yes. **Exercises.**
Batched small-solve layout and multi-RHS sharing of one factor (catalog C), plus pool reuse for per-cell lifted solves
(the allocator-cache lineage). None of A/B/D directly; this is the planning README's "factor once" state region
(`region-state-examples.md` S2/S3). **Hypothesis.** Today a **LOSS of about 3-10x** (allocation and copy dominate at q =
7). Parity is plausible with in-place multi-RHS solves. **Effort M**; the first step is one emit probe.

### N06 pairforce: antisymmetric pair force action, no stored pair matrix (check: OK)

```blade
function kernel(x: Array<Float64 like N, C3>) -> Float64 = {
    let w = method_for(x, x) <@> lambda(a: Array<Float64 like C3>, b: Array<Float64 like C3>) where comm(a, b) -> {
        let d0 = a((0 : C3)) - b((0 : C3))
        let d1 = a((1 : C3)) - b((1 : C3))
        let d2 = a((2 : C3)) - b((2 : C3))
        phi(d0 * d0 + d1 * d1 + d2 * d2)
    }
    let f = method_for(range<N>) <@> lambda(i) where omp(i: 1) -> {
        let xi = x(i)
        let f0, f1, f2 = object_for(<&!>) <@>
            (reduce(method_for(range<N>) <@> lambda(j) -> w(i, j) * (xi((0 : C3)) - x(j, (0 : C3))), (+)),
             reduce(method_for(range<N>) <@> lambda(j) -> w(i, j) * (xi((1 : C3)) - x(j, (1 : C3))), (+)),
             reduce(method_for(range<N>) <@> lambda(j) -> w(i, j) * (xi((2 : C3)) - x(j, (2 : C3))), (+)))
        let row: Array<Float64 like C3> = [f0, f1, f2]
        row
    } |> compute
    reduce(method_for(f) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p + q, 0.0)
}
```

The checked file also adds `+ sum_i f(i, 0)` to the checksum; drop it (Newton's third law makes `sum_i f_i` about 0, so
it cancels; keep it as a validation invariant). The expensive part is the symmetric `comm` weight with `phi(s) = exp(-s)
/ (1 + s)`. The antisymmetric part `p_ij = w_ij (x_i - x_j)` is exactly `-p_ji` (negation is exact, and `w(i, j)` is
read canonically, so it is bitwise symmetric). **Natural Fortran:** the directed double loop, N^2 phi evaluations.
**Expert:** the Newton-pair loop `do i; do j = i+1, n: w = phi; p = w*(xi - xj); f(:,i) += p; f(:,j) -= p`. Row r
receives contributions from earlier rows in ascending i, then its own row in ascending j. That is exactly the source's
ascending per-row order (the ordered-orbit insight, planning README), so the expert is order-identical to the spec in
repro, up to contraction. mt8: per-thread private force buffers merged in thread order, or pair-block colouring.
**Contract.** smoke n = 101; full n = 2503 (as `pairkernel`). Expert 3.13M phi evaluations. `checksum = sum f^2`, `input
= sum x`. Regimes serial/mt8 x licensed/repro/fastmath (exp). No twin. **Exercises item D directly.** A deferred `comm`
producer consumed by an ordered endpoint scatter: each unordered pair is evaluated once and fed +p to row i and -p to
row j in source order, and nothing is stored. That extends W2-A (a stored SymIdx pool) to a deferred producer and a
signed consumer, plus the S11 producer-orbit epilogue. The admission is `scheduleMayWrite` over the phi kernel, the
three leg kernels and the outer fold. Round-2: B3 compact row classes, the G7 join team (three legs share one j
traversal). **Hypothesis.** Today the deferred `w(i, j)` is re-evaluated per read: 3N^2 = 18.8M phi evaluations against
3.13M, a **LOSS of about 4-6x**. Forcing `w` instead stores 25 MB and pays 3N^2 canonical reads, about 1.5-2x, which
mirrors pairkernel's checksum pass. With item D's scatter, **parity** with the expert. Against natural it is a **WIN of
about 2x** (pair-once). **Effort S-M.**

### N07 segfir: two FIR consumers of one chunked axis (check: OK, resident)

```blade
type I = Idx<1009>          // SIZE n
type CI = Chunked<I, 97>    // SIZE kc
function kernel(x: Array<Float64 like CI>) -> Float64 = {
    let y1 = method_for(halo<CI, [-3, -2, -1, 0, 1, 2, 3]>) <@> lambda(w) where omp ->
        0.0625 * x(w(-3)) + 0.125 * x(w(-2)) + 0.1875 * x(w(-1)) + 0.25 * x(w(0))
        + 0.1875 * x(w(1)) + 0.125 * x(w(2)) + 0.0625 * x(w(3)) |> compute
    let y2 = method_for(halo<CI, [-8, -4, 0, 4, 8]>) <@> lambda(w) where omp ->
        0.5 * x(w(0)) - 0.25 * x(w(-4)) - 0.25 * x(w(4)) + 0.125 * x(w(-8)) + 0.125 * x(w(8)) |> compute
    prodsum(y1, y1) + prodsum(y2, y2)
}
```

Resident `Chunked` adds nothing: chunk edges are not boundaries and the halo is flat, so windows cross edges trivially.
The outputs are interior-only (n - 6 and n - 16). The catalog's ghost-reuse mechanism exists only on a zarr-streamed
source (`segmentRun`), and the bench harness has no provider regime. A file-backed variant is therefore a separate later
spec. **Natural Fortran:** two array-slice statements. **Expert:** one fused loop that loads x once into a 17-cell
register ring, writes y1 and y2, and accumulates the checksum. mt8: `parallel do` with a static schedule. **Contract.**
smoke n = 1009, kc = 97; full n = 2,000,003, kc = 65,537 (48 MB). `checksum = sum y1^2 + sum y2^2`, `input = sum x`.
Regimes serial/mt8 x licensed/repro. Inputonly twin: yes (bandwidth-bound). **Exercises.** Sibling-MAP fusion: two
stored halo outputs of different shrink over one input traversal, with aligned interior index spaces. This is W1-D's
sibling folds generalized to maps, plus W1-B's cell epilogue for the checksum. Item B: the shared-traversal half of the
region track. **Hypothesis.** Today a **LOSS of about 0.75-0.85** (x streamed twice: 4 streams against 3). Parity after
fusion. The ceiling is about 25%. **Effort S.**

### N08 mcpath: addressed Monte Carlo payoff and pathwise sensitivity (check: OK)

```blade
function step(t: Int64, st: Array<Float64 like Two, Path>) -> Array<Float64 like Two, Path> = {
    let z = r.normal_at(77, 1, t * np, np)
    method_for(range<Two, Path>) <@> lambda(c, p) -> {
        let g = 1.0 + 0.0005 + 0.02 * z(p)
        if Int64(c) == 0 then st((0 : Two), p) * g
        else st((1 : Two), p) * g + st((0 : Two), p) * 0.1 * z(p)
    }
}
// kernel: let rec traj: Array<Float64 like Step, Two, Path> = ... | prefix :: n -> prefix :: step(Int64(n) - 1, prefix(n - 1))
//         pay(p) = 0.99 * log(1 + exp(S_T(p) - 2)),  sens(p) = 0.99 * D_T(p) / (1 + exp(2 - S_T(p)))   (both |> compute)
```

The draws are regenerated per step from the address `(key 77, stream 1, t*np + p)`, so no tape is ever named. The
tangent is hand-derived because `ad.jvp` through a vector-state `let rec` is refused (`GradExpand.fs:1266`). (A language
gap.) **Natural Fortran:** stored random tape `z(np, nt)` filled first, then two state arrays stepped over t.
**Expert:** paths blocked, `(S, D)` kept in registers, and Philox4x32-10 plus Blade's uniform conversion and Box-Muller
**ported verbatim** (`src/cpp/rand_runtime.hpp:104, 465-536`). The port uses integer(8) masks for the 32-bit mulhi, with
no tape. mt8: `parallel do` over path blocks. libm `log` and `cos` can differ per toolchain, so agreement is to
tolerance, not bits. **Contract.** smoke np = 97, nt = 13; full np = 20,011, nt = 151 (cap-limited: Blade's trajectory
is 48.3 MB today). `checksum = sum pay^2 + sum sens^2 + mean(pay)`, `input = sum s0`. Regimes serial/mt8 x
licensed/repro. **fastmath is excluded**: vendor fast-math changes the transform, and so the draws. No twin. **Exercises
item A.** E-G5's admission generalized from "read at the cell being written" to "read within the cell's own fiber of the
previous row": cell (n, c, p) reads (n-1, c', p), which stays barrier-free when a thread owns all components of its p
range. Also: windowing (terminal-only demand), inlining the `normal_at` fill into the step map (no per-step z pool),
slice-write fusion for a multi-component pointwise step, and G49 (48 MB teardown in the timer). **Hypothesis.** Today a
**LOSS of about 1.3-2x** (Philox and Box-Muller dominate both sides). About parity after the transformations. **Effort
L**: the Philox port in Fortran and numpy is the cost and the risk.

### N09 tensorop: three ordered mode contractions (A (x) B (x) C) x (check: OK)

```blade
function kernel(x: Array<Float64 like I, J, K>, am: Array<Float64 like I, I>,
                bm: Array<Float64 like J, J>, cm: Array<Float64 like K, K>) -> Float64 = {
    let t1 = method_for(range<I, J, K>) <@> lambda(a, j, k) where omp(a: 1) ->
        reduce(method_for(range<I>) <@> lambda(i) -> am(a, i) * x(i, j, k), (+)) |> compute
    let t2 = method_for(range<I, J, K>) <@> lambda(a, b, k) where omp(a: 1) ->
        reduce(method_for(range<J>) <@> lambda(j) -> bm(b, j) * t1(a, j, k), (+)) |> compute
    let y = method_for(range<I, J, K>) <@> lambda(a, b, c) where omp(a: 1) ->
        prodsum(cm(c), t2(a, b)) |> compute
    // checksum: reduce over a of (reduce over b of prodsum(y(a, b), y(a, b))), outer kernel `where omp`
}
```

T1 and T2 must be computed. A deferred T1 would be re-evaluated per read, giving O(n^5). The spec therefore names them
as required intermediates, stored in every arm. An alternative library spelling exists, `m.mode_product(x, am, 0)`, a
serial synthesized nest, or `m.matmul(am, m.unfold(x, 0))`, which routes to the native `dgemm_nn`. It was not checked
here. **Natural Fortran:** `matmul` per slice (natural keeps intrinsics). **Expert:** each contraction in axpy /
outer-product order (`do i; T1(:,:,a) += A(a,i) * X(:,:,i)`, column-major so the reused axis is contiguous), or a
register-tiled GEMM over the unfolded view. Every cell keeps its ascending summation order (gram's lesson).
**Contract.** smoke 11/13/17; full n1 = 89, n2 = 97, n3 = 101 (X, T1, T2, Y = 4 x 7.0 MB; about 250M FMAs). `checksum =
sum Y^2`, `input = sum X + sum A + sum B + sum C`. Regimes serial/mt8 x licensed/repro/fastmath. No twin. **Exercises.**
A new contraction-order transformation, in none of A/B/D: recognize a builtin-`(+)` fold over one index of a map whose
reads are affine in it, and either interchange to the per-cell-order-preserving axpy nest or route the strided view to
the existing 6x8 `dgemm_nn` tile (which never splits a cell's sum). It is R-legal. The admission needs a pure fold
kernel and map body (`scheduleMayWrite`). **Hypothesis.** Today a **LOSS of about 3-10x**. The T1 inner fold strides
9797 doubles (X is 7 MB > L2) and T2's strides 101; Y's contiguous `prodsum` is at parity. After recognition, about
**parity** (ifx/gfortran `matmul` is strong). **Effort S-M.**

### N10 groupls: grouped Gauss-Newton local models (check: OK; codegen unverified)

```blade
function group_h(jg: Array<Float64 like P, S>) = method_for(jg, jg) <@> lambda(ra, rb) where comm(ra, rb) -> prodsum(ra, rb)
function kernel(t: Array<Float64 like G, S>, y: Array<Float64 like G, S>, th: Array<Float64 like G, P>) -> Float64 = {
    let r = method_for(range<G, S>) <@> lambda(g, s) where omp(g: 1) -> res(th(g), t(g, s), y(g, s)) |> compute
    let jt = method_for(range<G, P, S>) <@> lambda(g, a, s) where omp(g: 1) -> dres(Int64(a), th(g), t(g, s)) |> compute
    let cost = method_for(range<G>) <@> lambda(g) -> prodsum(r(g), r(g)) |> compute
    let grad = method_for(range<G, P>) <@> lambda(g, a) -> prodsum(jt(g, a), r(g)) |> compute
    let h = method_for(range<G>) <@> lambda(g) -> group_h(jt(g)) |> compute
    // checksum = sum cost + sum grad^2 + sum over g, a, b of h(g, a, b)^2 (logical cells)
}
```

The model is `f = th0 exp(-th1 t) + th2 t + th3` with a hand-derived, spec-fixed derivative (`dres`). Groups are
equal-sized (rectangular G x S). Ragged groups via `group_keys` support per-group scalars and dense rows (corpus
loops/150, 153), but a per-group compact SymIdx is unproven. The packed H is built from the SAME rows in adjacent `comm`
slots. **Codegen risk:** a compact pool per row-map cell (the open card "unforced map over a compact pool fails BL9002",
RESULTS 15.4). This source also evaluates `exp` 5 times per sample (in `res` and in each `dres`) where the expert does
it once. **Natural Fortran:** stored residual and Jacobian arrays, then `sum`, `matmul(J, r)`, `matmul(J, transpose(J))`
per group. **Expert:** one pass per group: `e = exp(-th1 t)` once, r and j in registers, cost, 4 gradient and 10
packed-H accumulators in registers, with groups threaded. **Contract.** smoke G = 7, S = 23; full G = 10,007, S = 61 (r
4.9 MB, jt 19.5 MB, about 34 MB in total). `checksum = sum cost + sum grad^2 + sum H^2`, `input = sum t + sum y + sum
th`. Regimes serial/mt8 x licensed/repro. Twin: no (exp-bound). **Exercises B + D + round-2.** It is an aggregate-only
grouped region: one differentiated producer feeding several summaries. That is W1-D's sibling-fold sharing over a shared
map producer plus a `comm` contraction leg, the G7 join team across groups, W1-B consumer epilogues, and CSE of `exp`
across the `dres` calls. Item D: a per-group packed `comm` output. The syrk tile is ineligible at p = 4. **Hypothesis.**
Today a **LOSS of about 2-4x** (the r and jt tapes plus 5x exp). Parity after fusion. **Effort M**; the first step is
one emit probe of the per-group compact H.

## 4. Effort, risks, and the drop experiment

| id | effort | main risk | drop it if |
|---|---|---|---|
| N01 | M (L with windowing) | no meaningful size until windowing lands; the cap size is L2-resident | at the tiled size the expert's time tile is < 10% faster than its own two-buffer sweep on this box |
| N02 | M | `ad.grad` in a function; route G declining to the 65536-cell scatter at full size | `blade plan` at full size shows route S (scatter), i.e. it is not the gather under test |
| N03 | M | LLVM fallback; runtime range per query | the two-pass and online experts differ < 10% (no state-specific mechanism to measure) |
| N04 | M | nested `let rec` emission cost dominates everything | the expert two-row loop and the natural full table differ < 1.5x (the frontier does not matter) |
| N05 | M | per-cell solve codegen (BL7xxx/BL9002) | the emit probe refuses and the only fix is a new batched-solve primitive (re-scope to item P) |
| N06 | S-M | 3N^2 deferred reads time out at full size today | the expert Newton loop is < 1.6x faster than the directed natural (the pair-once mechanism is invisible) |
| N07 | S | tiny ceiling (about 25%) | the expert fused loop is < 10% faster than natural's two statements |
| N08 | L | the Philox port; libm transform drift | an independent numpy port of `philox4x32_10` disagrees with Blade at bit level on 1e4 draws |
| N09 | S-M | none on the Blade side; the natural `matmul` may beat everyone | `m.matmul(A, m.unfold(X, 0))` already reaches parity, so the generic spelling is just a style choice |
| N10 | M | per-group compact H codegen | the emit probe refuses the per-group `comm` and the dense-H fallback hides the D mechanism |

## 5. Ranked list: add these first

Criteria: checks today, exercises a transformation this round plans (A/B/D), credible reviewed-expert twin.

1. **N06 pairforce.** Item D's next consumer (an ordered signed endpoint scatter from a deferred `comm` producer); the
   cleanest expert of the ten (the Newton-pair loop is order-identical to the source's rows); pairkernel's sizes, oracle
   and checksum discipline; a large, attributed gap today (6x phi evaluations).
2. **N10 groupls.** Items B (an aggregate-only grouped multi-summary region) and D (per-group packed H) on round-2's
   sibling-fold, join-team and epilogue machinery; an obvious expert (one register pass per group). Gate: one emit probe
   of the per-group compact H (if it refuses, that refusal becomes the D card).
3. **N09 tensorop.** Not an A/B/D item, but the cheapest high-signal add: one mechanism (order-preserving interchange
   onto the existing `dgemm_nn` tile), a trivial oracle (numpy einsum, same order), a textbook expert, a large predicted
   loss today; census material for item A next round.

Then: N07 (S, a B-adjacent sibling-map fusion; small ceiling); N08 (item A's E-G5 generalization; L for Philox); N02
(the access-description track; needs the function-scope probe); N01 (after windowing); N04; N05 (after the emit probe);
N03 (LLVM fallback, weakest mechanism).

## 6. File overlap with the other round-3 items

- Item C adds only `bench/kernels/<new>/{spec.md,kernel.json,<k>.blade,[inputonly.blade],natural.f90,expert.f90}`,
  `bench/validation/ref_<k>.py`, `pins-{A,B}.json` (shared: merge with A's pins), `bench/notes/kernel-list.md`,
  `bench/README.md` §5; no compiler file.
- The transformations the workloads exercise land in other items' files. **A:** `src/Optimize.fs` (slice-write fusion,
  E-G5 admission, row-fold fusion) and the `let rec` emission in `src/CodeGenBinding.fs` (N01, N04, N08). **B:**
  `src/Optimize.fs shareSiblingFoldsModule` and the grouping arms in `src/CodeGenCuda.fs` 2579-2833 (N10, N07, N03).
  **D:** `src/OrbitFold.fs`, the W2-A scatter emitters and `src/Deduce.fs` (N06, N10). N02's gather merge touches
  `src/GradNormalize.fs` / `src/GradSweeps.fs` (no overlap with A/B/D); N09's interchange touches
  `src/CodeGenExpr.fs:3787` (dgemm_nn) / `CodeGenExprSupport.fs:2065`, overlapping D only if D retunes the tile drivers.

## 7. Open questions for Codex (least-sure claims)

1. **N06 order identity.** Row j receives i < j ascending, then its own k > j, and `f_j += (-p)` equals `+ w*(x_j -
   x_i)`: bitwise only with contraction off. The repro Fortran keeps `-ffp-contract=fast`, so is "tolerance-equal" the
   honest claim?
2. **N06 evaluation count.** Does a deferred `comm` read `w(i, j)` in three `<&!>` legs evaluate phi 3N^2 times today?
3. **N05 / N10 / N03 codegen.** Does the C++ emitter accept `m.lu_solve` per kernel cell, a compact `comm` pool per
   row-map cell, and a runtime `0..cnt` in a threaded per-query kernel? (`check` does not run it.)
4. **N02.** Is it fair to drop the separate primal map and report `dw = F(x)` (an AD-derived primal) as y? And does
   route G admit the loss map's second array read `wt(q(0))` at full size, or decline to S (the 65536-cell cap)?
5. **N08 / E-G5.** Is the generalized admission ("reads confined to the cell's own p fiber of row n-1") barrier-free
   under the fixed ownership E-G5 uses, given that the c = 1 cell reads the c = 0 component of the previous row?
6. **N09 order.** Does `dgemm_nn`'s KC blocking keep each cell's ascending-k chain, bitwise the serial fold under
   `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, as the syrk tile does (KC blocks spilled and reloaded exactly)?
7. **N01 boundary.** Is a guarded `if` with checked `i - 1` reads the only full-extent spelling, or can a halo interior
   map plus a carried boundary ring avoid the BL8006 checks?
