# onepass — four power sums in one traversal (the `<&!>` reduction join)

Group B. Directory `bench/kernels/onepass/`.

## Object and operation

x ∈ [0, 2)ⁿ. The four power sums

    S1 = Σ x,   S2 = Σ x²,   S3 = Σ x³,   S4 = Σ x⁴      (x² = x·x, x³ = x²·x, x⁴ = x³·x)
    checksum = S1 + 2·S2 + 3·S3 + 4·S4

Each S_k accumulates in ascending k order in every `repro` arm. The Blade `<&!>` join
carries no THREADING licence (none can be expressed, kernels/README.md §4.4), but its `(+)` legs are
builtin-body folds: since the compiler flip (5f4b33ea) BOTH lanes reorder them by default —
the C++ lane gives the join the simd-reduction form with one multi-accumulator clause and
prints a census line (G7 partly fixed), the LLVM lane decorates the legs `reassoc` — so
`licensed` differs from `repro` in both lanes, and both are reported. Matched
permission (expert, licensed, behind `BENCH_LICENSED`): `!$omp simd reduction(+:s1, s2,
s3, s4)` on the single accumulation loop, and on the input-checksum fold over x — the four
accumulators and the input sum are builtin `(+)` folds in the Blade source,
reorder-licensed at the source regardless of which lane honours it today (README §3
matched-loop table). Output: the scalar. The quantity under test is
the number of passes over x.

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k | 10000019 | 0.0 | 2.0 |

(`hi − lo = 2`, a power of two.)

## Checksum

    checksum       = S1 + 2 S2 + 3 S3 + 4 S4     (all terms non-negative; x⁴ < 16)
    input_checksum = Σ x  (= S1; printed separately anyway)

## Sizes

| variant | n | x bytes | est. serial (one pass / four passes) |
|---|---|---|---|
| smoke | 50021 | 400 KB | < 1 ms |
| full | 5,000,011 | 40 MB | 8–15 ms / 20–40 ms |

Memory-bound at full size (DRAM-resident). Input share ≈ 15–20 ms of ~30 ms ≈ **50% → an
`inputonly` twin per arm REQUIRED** (kernels/README.md §3.3; incremental estimate reported beside the
total). R >= 21.

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (four-pass baseline)**: `s1 = sum(x); s2 = sum(x**2); s3 = sum(x**3); s4 =
  sum(x**4)` — four intrinsic passes (gfortran may or may not fuse them; the agent
  records). mt8: `!$omp parallel workshare` or four `parallel do reduction` loops. Named
  honestly (M1): a loop with four accumulators is routine Fortran too; this arm measures
  explicit fusion of passes, not expert skill.
- **Selection record**: baseline = four intrinsic sums as the spec lists them; expert =
  the Fortran agent's reviewed single loop with four accumulators.
- **expert**: one loop, four accumulators, powers by repeated multiplication. mt8: `!$omp
  parallel do reduction(+:s1,s2,s3,s4)`.

## Blade idiom

```blade sketch
let L = method_for(x)
let (s1, s2, s3, s4) = reduce((L <@> lambda(v) -> v) <&!> (L <@> lambda(v) -> v * v)
                          <&!> (L <@> lambda(v) -> v * v * v) <&!> (L <@> lambda(v) -> v * v * v * v),
                          lambda(u, v) where omp -> u + v, 0.0)
let checksum = s1 + 2.0 * s2 + 3.0 * s3 + 4.0 * s4
```

(examples/03 (b), the fused-pool shape.) Probed: one loop over x with four accumulators,
no pool allocated. **mt8 (round 2, gap G7):** the join's fold kernel carries the thread
REQUEST (`where omp` -- the spelling G7 named, and the same request the expert's
`!$omp parallel do reduction` makes); each leg's reorder licence is its own builtin `+`
(the policy: explicit request AND every leg's own licence, never OR-ed across legs; a
builtin-`(+)` join with no request opens no team, exactly as a single `reduce(a, (+))`
opens none). The C++ lane then threads the ONE sweep as a generalized Path B form: each
leg's original seed evaluated once before the team, the extent partitioned at the actual
team size (`blade_fold::chunk_lo`), four identity-seeded private accumulators per chunk
under ONE `omp simd reduction`, partials combined in ascending chunk order onto each seed
(`blade plan`: `fold form: threaded(Path B joined chunks, ...)` plus an applied `omp`
record). The explicit `, 0.0` seed is what the surface `(+)` seeds with (a lambda fold
kernel over a fused tree needs it spelled). The sibling-fold pass also joins the input
checksum into the same threaded traversal (every leg licensed). Earlier rounds: there was
no thread-level form and `omp` on the join's fold kernel was first dropped silently, then
(5f4b33ea) declined with a census line.

## Regimes

licensed and repro (both lanes reorder the four legs in licensed since the flip), fastmath;
mt8: blade-cpp threads the join (licensed, fastmath); repro keeps the four ascending
original chains (`serial (repro): k ascending original chains`) with only the input map
threaded; blade-llvm has no OpenMP (serial-only). The serial arms' `fold form:` lines are
unchanged by the `where omp` clause (it implies the simd form the join already took).

## Hypothesis

- vs natural (four-pass baseline): **WIN 2–3x** (one DRAM pass vs four).
- vs expert, `repro`: **equivalent** (same loop, four serial chains each).
- vs expert, `licensed`: **both Blade lanes equivalent** (all three vectorize the four
  accumulators; before the flip the C++ lane kept four serial chains — fixed).
- mt8 (licensed): blade-cpp's threaded join vs the expert's 8-thread reduction -- same
  one-team, one-sweep shape; measured by the runner (no forecast here).

## Risks

- The fuse gate must accept four legs; `blade plan` must show one traversal. If it splits,
  the result reverts to natural's.
- Powers: every arm must spell x⁴ as `((x·x)·x)·x` (no `x**4` → `pow`, no
  `(x²)²`); the Fortran agent checks that `x**4` under `-O3` is expanded as repeated
  multiplication in that order (it is usually `(x²)²`, which differs in rounding — if so,
  write the products explicitly in both tiers).
