# Memcheck census of the examples corpus — 2026-08-10

> **Post-fix addendum (same day, commit 874322c).** Three scope-tracker leak
> classes were closed after this census ran — function-body `let r = f(a)`
> registration (site 3b), return-argument pinning on FreshPool-call returns
> (value-first return + seed narrowing), and argument-position call
> temporaries (`g(f(x))` hoisting). Post-fix numbers:
>
> - 09_qg_atmosphere: 6.98 GB → **3.39 GB**, and the remainder matches its
>   three spectral trajectory module bindings + skeleton tables exactly —
>   per-timestep garbage is zero.
> - 08_burgers_les: 144 KB → **67 KB** (module bindings only).
> - The rest of Tier 2 was re-measured and is UNCHANGED — per-size
>   histograms show those numbers are predominantly large module-level
>   bindings (the deliberate compute-print-exit leak), e.g. physics/47's
>   entire 7.01 MB is its two rank-3 {6001,24,2} trajectory bindings, and
>   physics/44's 12.7 MB is 37 large stable blocks plus a 290 KB residual
>   of 64-byte blocks (4,534 of them — the one real class left: small
>   objects from tuple-returning helpers / heap extents tables, outside
>   computeFreshReturnFacts' single-array-return domain).
>
> Census-reading rule learned: a big outstanding number is NOT itself a
> leak verdict — match the live-block size histogram against the program's
> module-binding shapes first. Remaining documented-but-unfixed: the
> tuple-return / heap-extents small-object class (≤290 KB/program), and the
> `let rec` build-buffer double-materialize (both the build array and its
> final copy stay live — 2x the by-design footprint, e.g. ~1.5 GB of
> 09_qg's remaining 3.39 GB).

First full memory-leak census of `examples/` (11 top-level + 47 physics = 58
programs), run under the new `blade run --memcheck` Debug+AddressSanitizer
profile (branch `feat/debug-memcheck`). Every program compiled, ran to
completion, and reported allocator statistics; **no AddressSanitizer error
fired anywhere** — zero heap-buffer-overflows, use-after-frees, or double
frees across the corpus, so the `allocate<>`/`deallocate<>` teardown contract
(interior-free hazards and all) held on every program.

## How to read the numbers

`outstanding_bytes`/`outstanding_blocks` = heap still live at process exit,
measured from a post-startup baseline (ASan malloc/free hooks; the OpenMP
pool and stdio are excluded by construction). Module-level bindings
DELIBERATELY leak (see "Deterministic deallocation" in CodeGen.fs:
compute-print-exit, the OS reclaims), so small outstanding values are the
expected module-binding footprint, not bugs. The census signal is programs
whose outstanding scales with iteration count or allocation churn.

The corpus splits cleanly into three tiers:

**Tier 0 — module bindings only (37 programs, 350 B – 10 KB).** Everything
from `lsdft`/`lswosa`/`06_cg` (350 B / 2 blocks) up through the collision and
invariant-detector families. Outstanding matches the printed bindings'
sizes; alloc/free churn is fully reclaimed. No action needed.

**Tier 1 — elevated but plausibly structural (8 programs, 10–100 KB).**
`17_spectral_persistence` (20.5 KB), `28_rough_spin` (12.6 KB),
`07_subgrid_closure` (52.2 KB / 145 blocks), `32_calibration_ladder`
(74.3 KB), `04_trajectory_sensitivity` (87.5 KB / 18 blocks ≈ 4.9 KB each).
Worth a glance if these examples ever grow trip counts; not urgent.

**Tier 2 — real per-iteration leaks (13 programs, 129 KB – 6.98 GB):**

| program | outstanding | blocks | allocs→frees | shape |
|---|---|---|---|---|
| 09_qg_atmosphere | **6.98 GB** | 204,649 | 932,003→727,271 | ~34 KB/block; whole field arrays leaked per spectral timestep |
| physics/44_detector_survives_noise | 12.71 MB | 4,676 | 4.36 M→4.357 M | heavy churn, ~2.8 KB/block retained |
| physics/42_dynamical_q | 7.06 MB | 439 | 2.61 M→2.607 M | few large blocks (~16 KB each) |
| physics/47_flat_extension | 7.01 MB | 12,052 | 24,869→12,734 | **half of all allocations never freed** (~580 B each) |
| physics/46_ehrenfest_loop | 1.41 MB | 84 | 80 K→79.8 K | ~17 KB/block |
| physics/29_time_order_crossing | 1.27 MB | 219 | 110 K→109.9 K | also runs 577 s — brushes the 600 s memcheck cap |
| physics/31_free_deconvolution | 750.8 KB | 23 | 167 K→167 K | ~32.6 KB/block, few huge buffers |
| physics/30_observer_free_noise | 393.7 KB | 35 | 125 K→124.8 K | |
| physics/45_spectrum_of_the_law | 385.7 KB | 45 | 3,328→3,200 | |
| physics/43_cleaning_the_spikes | 333.6 KB | 49 | 123 K→123 K | |
| physics/34_collision_channels | 175.2 KB | 78 | 32.5 K→32.4 K | |
| 08_burgers_les | 144.1 KB | 217 | 909→609 | **a third of all allocations never freed** |
| physics/41_i3322_ceiling_certified | 129.1 KB | 13 | 47.4 K→47.3 K | |

Construct correlation across Tier 2: every entry is either **spectra-heavy**
(09: 27 fft/spectra references; 31: 8; 46: 4) or a **`let rec` time
trajectory** (44, 46, 47, 08_burgers). Both line up with the documented
deliberate exclusions in the deallocation tracker ("IIFE consumers drop the
list and leak"; spectra/provider outputs outside the registry) — these are
the tracker's known blind spots made quantitative, plus possibly genuine
escapes. Root-causing 09 (GB-scale, would OOM a longer run) is the highest-
value follow-up; 47 and 08_burgers (large *fraction* of allocations leaked,
low churn) are likely the easiest to localize.

## Census mechanics (for reruns)

- 4 parallel agents, each in a private copy of the examples tree (blade
  compiles next to the source file, so private copies are sufficient
  isolation); OMP_NUM_THREADS=4 per agent; OPENBLAS/CUBLAS routes disabled.
- The runner must `cd` to each .blade's own directory: CSV/zarr providers
  resolve `data/...` against the compiler process cwd at typecheck time.
- Two blade processes must never share a working directory (one process's
  post-compile cleanup deletes the other's intermediates mid-compile).
- Determinism spot-checked: repeated runs reproduce outstanding_bytes
  byte-for-byte.
- `allocs − frees − outstanding_blocks` is a constant per configuration
  (83 at OMP_NUM_THREADS=4): the pre-baseline allocation set. A deviation
  from the constant, not the constant itself, would be a finding.

## Incidental findings (not leaks)

1. **clang rejects signed subscripts on the array wrappers** that g++
   accepts (ambiguous between `operator[](size_t)` and the built-in
   subscript through the implicit data-pointer conversion). Hit 12 of 58
   examples. Fixed on this branch with exact-match integral overloads in
   nested_array_types.hpp; suite 4644/0.
2. **BL5400 misdirection on missing provider data**: with its zarr input
   absent, 09_qg_atmosphere fails spectra elaboration with "every axis
   extent must be statically known" — the real problem (data file not
   found relative to cwd) is upstream of the reported one.
3. physics/29 needs 577 s under memcheck (ASan + -O0) — any slower box or
   bigger trip count will hit the 600 s cap; bump via the runExecutable
   memcheck timeout if it starts flapping.
