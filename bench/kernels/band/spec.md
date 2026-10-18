# band — a banded operator enumerated in closed form (restored after G4)

Group A (structure the compiler derives). Directory `bench/kernels/band/`. Restored to the
suite on 2026-10-18 after agent C's fix of G4 (commit 1ea0ddf3, merged as 4b1a5224):
linear-conjunct `static struct` domains now stream as an affine nest in all three lanes
(`[domain-enum v1]` plan line; corpus `tests/corpus/domains/001-008`). Not yet measured;
blade-B and the Fortran agent write the sources after the merged binary is rebuilt.

## Object and operation

B is the n x n banded operator with half-width w:

    B(i, j) = φ(i, j)  for |i − j| <= w,   0 otherwise,   0 <= i, j < n
    φ(i, j) = (2 + d / (w + 1)) / (1 + d²),   d = i − j   (as a Float64; |d| <= w, so 1 < 2 + d/(w+1) < 3 and φ > 0)

φ is deliberately NOT symmetric in (i, j) (the `d / (w + 1)` term), so no arm may halve the
band by symmetry; it is rational, positive, and evaluated exactly as written: `d = Float64(i
- j)`, `(2.0 + d / Float64(w + 1)) / (1.0 + d * d)`. The operation is B applied to x and
contracted with x — the banded quadratic form

    s = xᵀ B x = Σ_{i} Σ_{j : |i−j| <= w} φ(i, j) · x_i · x_j

summed cell by cell in the band's lexicographic order (i ascending, j ascending within
each row) in `repro`; licensed folds may reorder. Output: the **scalar** `s` (no array
output). Why the contracted form and not the matvec y = Bx: a per-row fold over a domain
array has no spelling in Blade today (the domain's `reduce` is a flat fold over its keys),
so the matvec's named output could not be produced from the idiom; the contracted form is
the shape G4 fixed — a deferred fold over `range<Band>` — and the quantity under test is
the enumeration, not the output layout. (Recorded in the gap register as a note under
G4, not a new entry, until blade-B confirms.)

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k = i | 10000019 | 0.0 | 1.0 |

20k cells: input generation is negligible, **no inputonly twin**.

## Checksum

    checksum       = s                 (every term positive: φ > 0, x >= 0)
    input_checksum = Σ x

Expected magnitude at full size: ~n · (2w+1) · E[φ] · E[x]² ≈ 4e6 · ~0.1 · 0.25 ≈ 1e5 —
inside double range, relative drift from summation order ~1e-15.

## Sizes

| variant | n | w | band cells n(2w+1) − w(w+1) | dense visits n² | est. serial (band) | est. serial (dense natural) |
|---|---|---|---|---|---|---|
| smoke | 211 | 5 | 2,291 | 44,521 | < 0.1 ms | < 0.1 ms |
| full | 20011 | 101 | 4,051,931 (= 4,062,233 − 10,302; numpy and the plan agree) | 4.00e8 | ~20 ms (~5 ns per cell: one division) | ~0.3–1 s (predicate nest) |
| narrow (optional) | 20011 | 11 | 460,121 | 4.00e8 | ~2–3 ms | same |

n is set by the natural tier: its dense nest with a predicate visits n² cells and must stay
under the 2.5 s cap (4e8 visits ≈ 0.3–1 s). w = 101 rather than the ~11 first suggested so
that the band kernel itself (~20 ms) sits above the fresh-process timing floor; the
`narrow` variant keeps the original ~11 for a second point. Working set: x = 160 KB. No
extent is a power of two. Markers: `let static n = 20011   // SIZE n`, `let static w = 101
// SIZE w`; `Idx<n>`, `max=n-1`, `Float64(w + 1)` are derived.

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (dense-predicate baseline)**: the full n x n nest with `if (abs(i - j) <= w)`
  around the cell (the dense matrix B itself would be 3.2 GB, so the predicate nest IS the
  dense transcription here); `s` accumulated in the nest. mt8: `!$omp parallel do
  reduction(+:s)` on the outer i loop. This baseline is a straw man by construction (100x
  the visits) and is labelled so; the comparison of interest is vs expert.
- **expert**: `do i = 1, n; do j = max(1, i-w), min(n, i+w)` — the hand-written band
  bounds; may reorder by diagonal offset (`do d = -w, w; do i` over the valid range, inner
  loop contiguous in x) — same cells, same arithmetic per cell; in `repro` the summation
  order must be the band's lexicographic (i, j) order (the diagonal ordering is then NOT
  allowed; in `licensed` it is). mt8: `!$omp parallel do reduction(+:s)` on the outer loop.
- **Selection record**: baseline = the predicate nest (the literal "for all i, j, if in
  band"); expert = the Fortran agent's reviewed clipped-bounds nest; alternatives tried
  recorded in `notes/fortran-notes.md`.
- Matched-permission loops (licensed, behind `BENCH_LICENSED`): the expert's `s`
  accumulation loop (its Blade counterpart is a builtin `(+)` fold over the domain) and
  the input-checksum fold.

## Blade idiom

```blade sketch
let static n = 20011   // SIZE n
let static w = 101     // SIZE w
type Nx = Idx<n>
static struct Band {
    i: Int64<min=0, max=n-1>,
    j: Int64<min=0, max=n-1>
} where abs(i - j) <= w
function phi(i: Int64, j: Int64) -> Float64 = {
    let d = Float64(i - j)
    (2.0 + d / Float64(w + 1)) / (1.0 + d * d)
}
// inside the kernel function, x materialized:
let s = reduce(method_for(range<Band>) <@> lambda(i, j) -> phi(i, j) * x(i) * x(j), (+))
```

(The style guide's "iterate a constrained index domain" row.) `blade plan` must show
`[domain-enum v1] applied` for the `range<Band>` nest (the affine j-range per i, no key
table) and a `fold form:` line for the fold; the emit must show a two-level nest with
`j` running over `max(0, i-w)..min(n-1, i+w)` and NO `sparse_index_t` / hash build. The
subscripts `x(i)`, `x(j)` take `Int64` domain coordinates into an `Nx`-tagged array: they
carry a BL4003 advisory and a run-time check (README §3.10) unless x is held as an
anonymous array or the coordinates are cast — blade-B picks the spelling the style guide
ranks highest and records whether the checks survive in the emit. mt8: the fold licence
goes on the fold kernel (`lambda(p, q) where omp -> p + q, 0.0`, README §7.4) and the
domain nest's outer level must then thread — blade-B confirms the pragma lands on the `i`
level (`[omp v1]` line) or records "no parallel form".

## Status after the sources landed (commit a879ef1a; blade-B `llvm-acceptance-B.md` §7)

- In the suite: `band.blade`, `natural.f90`, `expert.f90`, `kernel.json`, pins-B entry,
  `ref_band.py`, dump + validate arm; smoke-verified in both lanes and both FP regimes; the
  full-size plan is the closed-form affine nest with NO key table (G4 fixed). Not yet
  measured at full size (the re-measure follows the wave-2 merges).
- The domain fields had to be `Int64<min=0, max=n-1>`: the corpus's `Int<...>` (Int32)
  spelling typechecks, but then the LLVM lane refuses `phi`'s Int32 parameters (noted under
  G4).
- **mt8: Blade "no parallel form"** — `[omp v1] kernel: declined -- a closed-form domain
  nest has no thread-level form yet` (register G31; `kernel.json no_parallel_form: true`).
  The Fortran arms run mt8.
- **G30 (new):** the C++ lane does NOT vectorize the closed-form domain's inner loop —
  blade-llvm vectorizes it 4-wide unrolled (8 `vdivpd` per iteration); blade-cpp emits a
  scalar inner loop despite the `omp simd reduction` pragma (2 `vdivsd` per cell, 12
  instructions); no casts, Int32 fields and an inlined `phi` were all tried and stay scalar,
  so the blocker is not the BL8006 checks, the frame or the index width (g++'s vectorizer
  remarks name only the cast check's panic). Expected: blade-llvm equivalent to the expert,
  blade-cpp losing.
- The Fortran agent's rejected alternative for the repro expert (a `phi(d)` lookup table)
  is recorded in `fortran-notes.md`; this spec treats the per-cell division as part of the
  kernel, so the expert keeps it per cell.

## Regimes

licensed (serial, mt8 — Fortran arms only, Blade "no parallel form"), repro (serial, mt8
likewise). fastmath not run (one division per cell; nothing a blanket licence would change
beyond the matched fold). `licensed` differs from `repro` in the fold form only.

## Hypothesis

- vs expert: **blade-llvm equivalent; blade-cpp a loss** (G30: the C++ lane's scalar inner
  loop against the expert's and the LLVM lane's 4-wide division) — both visit exactly the
  band's 4.05M cells with the same
  per-cell arithmetic; the only candidates for a difference are loop overhead at the
  per-row bounds (affine in both) and whether each compiler vectorizes a loop body with a
  division. A loss > 10% would point at the domain nest's lowering (bounds recomputation,
  check-carrying subscripts).
- vs natural (predicate nest): **win ~50–100x** on totals — a measurement of the
  enumeration mechanism (visiting the solutions only), reported with the straw-man label.
- mt8: equivalent if the domain nest's outer level threads; else "no parallel form".

## Risks

- The domain nest's `j` bounds must come out affine (G4's `[domain-enum v1]`); if the plan
  shows a key table (`SkDomain` hashed route), the kernel is back in the pre-fix state and
  is reported as such, not re-spelled.
- Subscript checks on `x(i)` / `x(j)` inside the inner loop (BL8006) could cost ~1 ns per
  read; the emit says whether they are hoisted.
- The LLVM lane: G4 says all three lanes stream; blade-B smoke-probes it.
- The non-symmetric φ must be copied exactly in every arm (operation order matters for
  the 1e-12 tolerance in repro; licensed reorders only the sum).
