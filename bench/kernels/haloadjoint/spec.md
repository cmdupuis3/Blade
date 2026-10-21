# haloadjoint — a 1-D weighted nonlinear halo stencil, its primal and its reverse action Jᵀw

Group B (language-level structure). Directory `bench/kernels/haloadjoint/`. Round 3, wave 2
(W2-5), catalog workload N02 (`round3/research/C-workloads.md` §N02) as corrected by the
Codex review C (finding 2: the strict derivative reference must be an explicit ordered
update list). It is the item's FALLBACK: the item was N10 `groupls` (grouped Gauss-Newton
local models), whose emission gate failed — a row map whose cell is a per-group packed
`comm` H does not run in any lane (C++ `error[BL8005]: std::bad_array_new_length`, the
LLVM lane refuses "a compact output whose group extents disagree with the loop levels" and
falls back to C++, the interpreter panics BL8003 (main-checkout binary, via `blade_eval`);
gap register G67). The reproducers, with the binary / tree identities and the diagnostics
observed per lane in their headers, are `bench/notes/repro/W2-5-G67-grouped-packed-rowmap.blade`
(minimal) and `bench/notes/repro/W2-5-G67-n10-groupls.blade` (the corrected N10 source). N10
waits for G67; a dense-H stand-in would not test its mechanism (review C, N10 verdict).

This is the access-description track (structural 02, the halo reverse rule), not items
A / B / D: what it measures is the shape of Blade's reverse sweep for a halo map.

## Object and operation

x, w ∈ [0, 1)ⁿ (0-based). One nonlinear 5-point stencil over the interior centres:

    F_c(x) = ((x(c−1) · x(c+1)) + (sin(x(c)) · x(c+2))) − ((0.5 · x(c−2)) · x(c−2))     c = 2 .. n−3

(the parenthesization is the source's: `x(q(-1)) * x(q(1)) + sin(x(q(0))) * x(q(2)) - 0.5 *
x(q(-2)) * x(q(-2))` parses this way). Two required outputs (the only ones), both
materialized:

- **y** = F(x) over the interior: n − 4 cells, `y(c)` for c = 2 .. n−3.
- **dx** = Jᵀw = ∂L/∂x for the weighted loss L = Σ_c w(c) · F_c(x) (w read at each window's
  centre): n cells. Every cell receives at least one window's contribution (x(0) is the −2
  neighbour of the window centred at 2, x(n−1) the +2 neighbour of the one centred at n−3),
  so **dx has no zero boundary** (the item brief's "zero boundary" belongs to dw, below).

Blade computes dx with `ad.grad(loss)(x, w, dx, dw)` (reverse mode), the catalog's "array
VJP" through the scalar-loss route (review C: the map-in-`ad.vjp` spelling is a mode-plumbing
gap, not this kernel's subject).

## The derivative reference: an explicit ordered update list

The reference is NOT an independent closed formula: the review's point (C/2) is that the
strict chain is the reverse sweep's actual sequence of rounded updates, and that the two
occurrences of x(c−2) in `(0.5 · x(c−2)) · x(c−2)` are two updates, not one `−w · x`. The
list below is read off the emitted sweep (`loss__grad` in the C++ lane at 667250cd; the LLVM
lane lowers the same AD expansion) and frozen here; both Fortran twins and the oracle
implement exactly it.

**dx starts at +0** (the buffer `method_for(range<I>) <@> lambda(i) -> 0.0`; ad.grad
accumulates into it). Then, for every destination j, the updates below are applied **in this
order**, each a separately rounded `dx(j) = dx(j) + term`, each present only when its window
exists (centre c = j − offset in 2 .. n−3; a missing window is skipped, never a +0 add):

| # | offset | centre c | term (rounding points as parenthesized) | present for j in |
|---|---|---|---|---|
| U1 | +2 | j − 2 | `(s · w(c)) · sin(x(c))` | 4 .. n−1 |
| U2 | +1 | j − 1 | `(s · w(c)) · x(c−1)` | 3 .. n−2 |
| U3 | 0 | j | `((s · w(c)) · x(c+2)) · cos(x(c))` | 2 .. n−3 |
| U4 | −1 | j + 1 | `(s · w(c)) · x(c+1)` | 1 .. n−4 |
| U5 | −2 | j + 2 | `((−(s · w(c))) · x(c−2)) · 0.5` — the LEFT occurrence, through `0.5 · x` | 0 .. n−5 |
| U6 | −2 | j + 2 | `(−(s · w(c))) · (0.5 · x(c−2))` — the RIGHT occurrence | 0 .. n−5 |

s is the cotangent of the loss's fold at the window, the seed 1.0 (`__v51[m] = 0.0 + 1.0`),
so `s · w(c)` is exactly w(c) and `−(s · w(c))` exactly −w(c); the twins write w(c). With
every term present (4 ≤ j ≤ n−5):

    dx(j) = (((((( +0 + w(j−2)·sin x(j−2) ) + w(j−1)·x(j−2) ) + (w(j)·x(j+2))·cos x(j) )
                 + w(j+1)·x(j+2) ) + ((−w(j+2))·x(j))·0.5 ) + (−w(j+2))·(0.5·x(j))

Each term is the analytic partial derivative of w(c)·F_c (∂F/∂x(c+2) = sin x(c), ∂F/∂x(c+1) =
x(c−1), ∂F/∂x(c) = cos(x(c))·x(c+2), ∂F/∂x(c−1) = x(c+1), and ∂F/∂x(c−2) = −x(c−2) as two
halves), so the list is Jᵀw; `ref_haloadjoint.selfcheck()` confirms it against a central
finite difference of L at eleven cells (worst relative difference 1.2e-8 at h = 1e-6).

**How the emitted sweep forms it.** Route G (`GradNormalize.haloGatherPlan`, the gather
default `BLADE_AD_HALO_GATHER` unset): one destination-indexed pass over j = 0 .. n−1 PER
ASSIGNMENT, in descending offset, each `__g_x[j] = valid ? __g_x[j] + term : __g_x[j]` —
seven passes: U1, U2, then the dw pass (offset 0, `dw(c) += s · F_c`), then U3, U4, U5, U6.
Descending offset is ascending centre, so a scatter over centres ascending (`dx(c+o) +=` for
o = +2, +1, 0, −1, −2, −2 at each c) gives every cell the same order; the stable order within
offset −2 is the source order of the product's two operands (left, then right).

**Verified** (smoke n = 1013). `bench/validation/dump/haloadjoint_order.blade` spells the
list by hand and compares it with ad.grad's dx by EXACT NUMERIC EQUALITY (`==`, cell by cell,
in the same program, so both sides call the same libm; `==` does not distinguish +0 from −0,
and no raw-bit comparator was run on the Blade side): `mismatches = 0`, `dw_mismatches = 0`,
`boundary_dw = 0.0` in the C++ lane and the LLVM lane under `BLADE_FP_REPRO=1
BLADE_FP_CONTRACT=off`, and also under the default contraction; the interpreter gives
`mismatches = 0` (through the blade MCP server's `blade_eval`, whose binary is the main
checkout's, not this tree's). Both gfortran twins built with `-ffp-contract=off -DBENCH_DUMP`
(serial, and the expert also mt8-licensed) are BITWISE equal to the ordered oracle in every y
and dx cell (2022/2022, compared as raw 64-bit patterns of the dumps' 17-digit renderings,
which round-trip exactly; so gfortran's sin / cos agree with the C runtime's on these inputs);
with `-ffp-contract=fast` they differ from it by at most 4.4e-16 relative. Both ifx twins
(`/O3 /QxHost /fp:precise /Qopenmp-simd`, which does not contract) differ from it in 23 y and
18 dx cells by at most 2.3e-16 relative (Intel's sin / cos).

**Contraction.** The equality statements hold with contraction OFF. Every regime of this suite
compiles with contraction ON (g++ / gfortran `-ffp-contract=fast`, ifx `-mllvm
-fp-contract=fast`, LLVM per-instruction `contract`), where `dx(j) + a·b` may become one FMA,
differently per compiler and context; the arms are then tolerance-equal (1e-12 on the
checksum, measured ≤ 4.4e-16 per cell), not bitwise — including in repro (README §3; repro
fixes the summation order, not contraction).

## Accounting: what the Blade arm computes beyond the spec

The required benchmark outputs are y and dx only; neither Fortran twin computes anything
else. The Blade arm also computes two kinds of extra work, which stay in its timed region:

**Source-named, AD-required auxiliaries** (named in this frozen source; their computations
and stores stay — hard constraint 1 lets only compiler-inserted temporaries disappear):

- **d**, the loss tape `let d = ... |> compute` in `loss` (n − 4 cells, w·F, one sin per
  cell), named and forced by the source; reverse mode differentiates through it.
- **dw**, the buffer `let mut dw` the source creates for ∂L/∂w: `ad.grad` differentiates EVERY
  Float64 array parameter (`GradNormalize.classifyParam`), so the call fills it — n cells, F_c
  on the interior (`dw_mismatches = 0`: equal, `==`, to a hand-spelled F_c), zero on the four
  boundary cells; one pass with one sin per interior cell.
- **lv**, the loss value the source binds (`let lv = ad.grad(..)(..)`, unused): the fold of d.

**Synthesized by the reverse sweep** (compiler-created; not source storage):

- **d's cotangent** (n − 4 cells, all 1.0, the fold's seed), whose zero initializer is an
  (n − 4)-element literal copied from .rodata (G68), and the sweep's index / validity
  bookkeeping.
- in the seeding loop the forward tape is re-added into the (already copied) loss
  accumulator — a dead second fold that g++ may delete.
- the plain `loss` function (never called) is emitted too.

Removing d, dw or lv is NOT permitted under this source: a future spelling without them (a
`wrt` selector, an `ad.vjp` of the map) is a source / protocol revision with a fresh baseline.
Sharing their COMPUTATIONS with the required outputs (below) is a legal transformation that
keeps their stores.

**Transcendental calls** (source level, in the emitted C++ / the Fortran sources; the machine
code is not audited, and these are counts, not measured call evidence):

| arm | sin | cos | total | smoke (n = 1013) | full (n = 50,021) |
|---|---|---|---|---|---|
| Blade (this emission) | 4(n − 4): y, the tape d, the dw pass, U1 | n − 4: U3 | 5(n − 4) | 5045 | 250,085 |
| natural | 2(n − 4): y, U1 | n − 4: U3 | 3(n − 4) | 3027 | 150,051 |
| expert | n + 2B | n − 4 | 2n + 2B − 4 | 2024 | 100,136 |

B = ⌈(n − 8)/1024⌉ blocks (1 at smoke, 49 at full): each block's sin strip recomputes its two
overlap cells, and the peeled boundary computes its own (y at 2, 3, n−4, n−3; U1 at the four
tail cells), so the expert is APPROXIMATELY two calls per cell. Blade's four sin calls per
window revisit the same interior arguments (x(c) in y, d, dw and U1): that is this emission's
redundant primal / transcendental work — missed sharing across passes — not a cost inherent in
Jᵀw or a lower bound of `ad.grad`.

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k | 10000019 | 0.0 | 1.0 |
| w | (n) | k | 20000038 | 0.0 | 1.0 |

(x is the same as twofir's / fusedchain's x at the same n.)

## Checksum

    sy = Σ_c y(c)²     (c = 2 .. n−3)   sd = Σ_j dx(j)²     (j = 0 .. n−1)
    two separate chains, each seeded with +0.0, ascending; checksum = sy + sd
    input_checksum = Σ x + Σ w           (two chains, ascending, then one add)

Both checksum terms are sums of squares (non-negative). Every input is a multiple of 2⁻³¹
below 1 and the partial sums stay below 2¹⁶, so the input sums are exact in every order and
every arm's `input_checksum` equals the pin exactly. Pins: smoke checksum 931.84702902803338,
input 994.20763162942603; full 49031.582053620172, 49973.552964067087 (`pins-B.json`).

## Sizes

| variant | n | x, w | y | dx | principal payload (spec outputs + inputs) |
|---|---|---|---|---|---|
| smoke | 1013 | 2 × 7.9 KB | 7.9 KB | 7.9 KB | 32 KB |
| full | 50,021 | 2 × 0.40 MB | 0.40 MB | 0.40 MB | **1.6 MB** |

**The full size is capped by the compiler (gap G68), not by the 64 MB rule** — the shipped
payload (1.6 MB required, ~2.8 MB in the Blade arm) is far below it. The catalog's n =
1,000,003 does not compile in useful time: the reverse sweep's zero buffer for the tape's
cotangent is an (n − 4)-element array LITERAL (`Grad.fs` C6 → `GradExpand.zerosOfDims`), and
the `lower` phase (which includes the optimizer passes; no hotspot was isolated) is
super-linear in its length — `BLADE_PHASE_TIMING=1 blade emit`: emit 3 / 6 / 24 / 97 s at n =
5003 / 20011 / 50021 / 100003 (`lower` 19 s at 50021, 95 s at 100003); at 1,000,003 the
compiler was killed after 627 CPU s without finishing. Each Blade arm runs the front end twice
(compile + plan), so n = 50,021 keeps a full-size build near 50 s per arm (`run.py check
haloadjoint --size full --regimes serial-licensed-native,serial-repro-native`: every arm and
twin built and validated against the full pin, 338 s for the whole check). n is odd and not a
power of two.

At the catalog's n = 1,000,003 the four required arrays alone (x, w, dx: n cells; y: n − 4)
are 32,000,064 bytes; with dw, d and d's cotangent the Blade arm's arrays are 56,000,072
bytes, plus 7,999,992 bytes for today's literal image — 64,000,064 bytes if all were resident
together, before metadata and thread scratch. So the size does NOT follow automatically from
a G68 repair: after the fill repair, a fresh per-arm peak-live / store audit (lifetimes, image
residency, scratch) decides the next full size.

**Storage estimate per arm (full size).**

| arm | named / required arrays | extra | estimate |
|---|---|---|---|
| blade-cpp | x, w, y, dx | dw, tape d, d's cotangent (+ its 0.40 MB .rodata initializer); checksum folds stream; mt8 Path B partials | ~2.8 MB + 0.4 MB image |
| blade-llvm | as blade-cpp (same AD expansion) | not audited beyond the smoke run | ~2.8 MB |
| natural | x, w, y, dx | none | 1.6 MB |
| expert | x, w, y, dx | two strips of 1024 doubles (16 KB; per thread in mt8) | 1.6 MB |
| every inputonly twin | x, w | none | 0.8 MB |

Everything is L2/L3-resident at full size: the kernel is transcendental- and pass-bound, not
DRAM-bound. Input share: two generator fills against roughly 2–5 transcendentals per cell; in the
check session's single smoke runs (not a measurement) the twins were ~15–30% of their arm's
region → **an `inputonly` twin per arm is REQUIRED** (kernels/README.md §3.3).

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (the literal transcription of the update list)**: one loop for the primal; dx = 0;
  then ONE LOOP PER UPDATE in list order (U1 … U6), each over its destination range (`do j = 4,
  n−1: dx(j) = dx(j) + w(j−2)*sin(x(j−2))`, …) — every cell gets its contributions in the
  list's order; then one sum loop per checksum term and the two input sums. Arrays 0-based
  (`x(0:n−1)`, `y(2:n−3)`), every expression parenthesized as above (Fortran honours
  parentheses). mt8: `!$omp parallel do` on every loop (the update loops are destination-
  indexed, so race-free) and `reduction(+:)` on the sums. No other directives.
- **expert (one fused destination-owned pass)**: per block of 1024 cells, a strip
  `sn = sin(x(jb−2 .. je))` and `cn = cos(x(jb .. je))` (independent loops with plain `!$omp
  simd`: vectorizable where the compiler has a vector libm, i.e. ifx's SVML; gfortran calls
  the scalar libm), then ONE loop writing y(j) and dx(j) from the same loads — dx(j) as one
  parenthesized expression +0, U1 … U6 (no read-modify-write passes over dx) — and
  accumulating sy and sd from the stored cells in the same iteration. The 8 boundary cells
  (j = 0..3, n−4..n−1, the only ones with a missing window) are peeled (`dxcell(j)` applies the
  list with its guards; y(2), y(3), y(n−4), y(n−3) likewise), so the sums still run in
  ascending j (head, blocks, tail). mt8: `!$omp parallel do reduction(+:sy, sd)` over the
  blocks (strips private). Approximately one sin and one cos per cell: sin n + 2B, cos n − 4
  (the table in "Accounting").
- **Alternatives (not these arms)**: the textbook hand adjoint — a SCATTER over centres
  ascending, six `dx(c+o) +=` per centre — has the same per-cell order but cannot take a plain
  `parallel do` (a race); a scalar 3-cell sin ring (sin once per cell, no strips) serializes
  the loop and blocks vectorization of the libm calls.
- **Selection record**: natural = the per-update loop transcription; expert = the fused
  block-strip pass (W2-5, 2026-10-21; not yet seen by the Fortran reviewer). gfortran 16.2
  warned `-Waggressive-loop-optimizations` ("iteration 341 invokes undefined behavior") on an
  earlier expert whose licensed `simd` loop privatized scalar temporaries inside the
  `parallel do`; the committed form has no scalar temporaries in that loop and compiles
  warning-free (`-Wall`) in all four flag sets.

## Blade idiom

```blade sketch
type I = Idx<50021>   // SIZE n
function loss(x: Array<Float64 like I>, wt: Array<Float64 like I>) -> Float64 = {
    let d = method_for(halo<I, [-2, -1, 0, 1, 2]>) <@> lambda(q) ->
        wt(q(0)) * (x(q(-1)) * x(q(1)) + sin(x(q(0))) * x(q(2)) - 0.5 * x(q(-2)) * x(q(-2))) |> compute
    reduce(d, (+))
}
// in kernel(x, wt):
let y = method_for(halo<I, [-2, -1, 0, 1, 2]>) <@> lambda(q) where omp ->
    x(q(-1)) * x(q(1)) + sin(x(q(0))) * x(q(2)) - 0.5 * x(q(-2)) * x(q(-2)) |> compute
let mut dx: Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp -> 0.0 |> compute
let mut dw: Array<Float64 like I> = method_for(range<I>) <@> lambda(i) where omp -> 0.0 |> compute
let lv = ad.grad(loss)(x, wt, dx, dw)
let sy = reduce(method_for(y) <@> lambda(v) -> v * v, lambda(p, q) where omp -> p + q, 0.0)
let sd = reduce(method_for(dx) <@> lambda(v) -> v * v, lambda(p, q) where omp -> p + q, 0.0)
```

Spelling constraints met on the way (this tree): the loss map may carry no `where` clause
(BL5500 "reverse mode cannot lower a map whose kernel carries a `where` clause"); the loss
fold must read a named array (`reduce(method_for(halo ..) <@> .., (+))` inline is BL5500
"deferred/former reductions are not differentiable"; an unforced `let d` takes the same
route, literal included);
the halo inner must be a LITERAL `Idx<n>` (an `Idx<n>` of a `let static` is BL5500), which is
why the SIZE literal sits in the type. The checksum folds and the input sums are twofir's
committed spellings (`axes = 1` for the input sums, gap G55).

`blade plan` (C++ lane, smoke builds of `run.py check`, this tree 667250cd):

- serial-licensed: `[fold-form v1] __v141 / __v148 applied [fold form: simd K-lanes(8)
  self-dot ...]` (sy, sd); `[fold-form v1] __v214 applied [fold form: simd-reduction;
  reduction join of 2 legs]` with `[sibling-folds v1] input_sum: applied [2 folds over {x, wt}
  share one traversal ...; per-leg licences kept]`; the `[omp v1]` requests decline with the
  census line (`BLADE_OMP_THREADS=1`).
- mt8-licensed: `[omp v1] ... applied [BLADE_OMP_PARALLEL_FOR; loop nest level 0]` for the
  generator (`__ret..`), the primal y (`__v121`) and the two buffer fills (`__v127`,
  `__v133`); sy, sd `threaded(Path B chunks, simd K-lanes(8) self-dot per chunk)`; the input
  sums `threaded(Path B joined chunks, 2 builtin + legs, ...)`.
- repro (serial, mt8): every fold `serial (repro)`; in mt8 the maps stay threaded and the
  fold requests print `[omp] requested but emitted serial: BLADE_FP_REPRO=1 ...`.
- Every regime: `[cell-epilogue v1] __v141 / __v148 declined -- __v134 (the ad.grad call)
  is not repeatable / reads __v127 between the producer and the fold [... (two passes)]`;
  `[sibling-folds v1] kernel: declined -- no two folds share one dense axis (extents 1009,
  1013)`; `[pool-reuse v1] kernel: declined`.
- **The reverse sweep has no plan record at all**: the route (G, gather) and its seven
  passes are visible only in the emitted C++; the forward fold INSIDE `loss__grad` is an
  explicit serial loop (the AD expansion lowers `reduce(d, (+))` into a loop, so its builtin
  licence is lost and nothing says so); the record `[fold-form v1] loss applied [K-lanes(8)]`
  is the never-called plain `loss`.
- The LLVM lane accepts the source (no fallback), emits the maps serially and reassociates
  the four licensed folds in licensed (`one accumulator chain with reassoc nsz`).

At full size (n = 50,021) the emitted sweep is the same: seven destination passes over 50021
cells, the `static const double __v51__init[50017]` initializer (route G, not the 65536-cell
route-S backstop).

**What this workload measures.** Today Blade runs the primal map, the loss-tape map and its
fold, then seven serial destination passes (six read-modify-writes of dx, one of dw), plus two
checksum passes — about ten passes and 5(n − 4) transcendental calls — where the expert makes
one fused pass with approximately two calls per cell. The transformations, in the order they
pay: (1) merge route G's per-assignment passes into ONE destination pass that applies U1 … U6
in list order (same per-cell chain, so strict-legal; `GradSweeps.haloGather`); (2) share the
primal work across passes — F and sin(x(c)) are evaluated for the same window in y, d, the dw
pass and U1 — by cross-pass CSE / destination fusion, while still STORING the source-named
y, d and dw (sharing computations is legal; deleting the forced d or dw stores is not, under
this frozen source); (3) the P2 independent-iteration mark so the destination pass threads
(structural 02 §3.3.3), and a licence inside a differentiated function for the gather route
(BL5500 today); (4) G68, after which a fresh per-arm peak-live / store audit decides the next
size. Dropping dw / lv altogether needs a different source (a `wrt` selector or an `ad.vjp`
spelling) and therefore a protocol revision and a fresh baseline; it is not a transformation
of this kernel. Any fused form must keep y and dx stored, keep the list's per-cell order in
repro, and keep sy and sd separate +0 chains.

## Regimes

licensed (serial, mt8), repro (serial, mt8). No fastmath: the vendor's blanket permission
would reorder each cell's six-term chain and swap in fast libm, a different algorithm from
the list; that comparison needs its own spec.

**Matched-permission loops (licensed, expert tier, behind `BENCH_LICENSED`)**: the sy / sd
accumulation in the fused loop (`simd reduction(+:sy, sd)`) and the input sums (`simd
reduction(+:sx, sw)`) — four licensed folds in the Blade source. The dx chain is never
reordered in any arm (in Blade it is not a fold at all but a sequence of passes). The strips
carry a plain `!$omp simd` in every regime (no reduction). The natural tier has no directives
beyond the mt8 `parallel do`s. Repro: no `simd reduction` anywhere; the mt8 Fortran arms keep
their thread-level `reduction(+:)` (the regime's recorded asymmetry, README §3).

## Hypothesis

- vs natural: **LOSS, ~0.5–0.8 today** — both make one pass per update; Blade adds the tape
  map and its fold and the dw pass (5(n − 4) vs 3(n − 4) transcendental calls), and its
  adjoint is serial in mt8 where natural threads every pass.
- vs expert: **LOSS, ~0.35–0.6 today** (5(n − 4) calls vs approximately two per cell, ~10
  passes vs 1; ifx's expert may vectorize its strips with SVML, widening it). After
  transformations (1)–(2) the Blade arm still computes and stores d and dw and folds lv (the
  source names them): the remaining serial gap is those stores and that fold, unestimated;
  mt8 additionally needs (3). These are estimates from call and pass counts, not
  measurements.
- In the check session's single smoke runs (n = 1013, NOT a measurement) blade-cpp took ~1.9×
  the gfortran expert's region and ~1.75× the natural's.

## Risks

- Store audit (README §1.5): the expert stores y and dx in the loop that reads them back for
  the sums; the `-DBENCH_DUMP` build reads them after the stop line, the timed build does not
  — the full-size objdump audit decides.
- libm: the arms call different sin / cos (ucrt / mingw for g++ and gfortran, Intel's libm
  and SVML for ifx); the per-cell values may differ in the last place. The 1e-12 checksum
  tolerance absorbs it; the equality statements above are made only where the libm is
  shared (the Blade order check: one program; gfortran vs the oracle: the same C runtime
  values, observed).
- The incremental (total − twin) estimate is a difference of two medians of similar size at
  this small n; its interval may be wide.
- The kernel cannot fail at run time (every window read is in range by construction; the
  BL8009 halo extent checks run before the first map), so a fused form changes no failure
  point.
