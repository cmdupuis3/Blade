# onepass — four power sums in one traversal (the `<&!>` reduction join)

Group B. Directory `bench/kernels/onepass/`.

## Object and operation

x ∈ [0, 2)ⁿ. The four power sums

    S1 = Σ x,   S2 = Σ x²,   S3 = Σ x³,   S4 = Σ x⁴      (x² = x·x, x³ = x²·x, x⁴ = x³·x)
    checksum = S1 + 2·S2 + 3·S3 + 4·S4

Each S_k accumulates in ascending k order in every `repro` arm. The Blade `<&!>` join
carries no THREADING licence (none can be expressed, README §7.4), but its `(+)` legs are
builtin-body folds: under `BLADE_FP_REASSOC=1` the LLVM lane decorates them `reassoc`
(blade-B F11) while the C++ lane leaves the join's accumulation serial — so `licensed`
differs from `repro` in the LLVM lane only, and both Blade lanes are reported. Matched
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
`inputonly` twin per arm REQUIRED** (README §5.3; incremental estimate reported beside the
total). R >= 21.

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

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
                          <&!> (L <@> lambda(v) -> v * v * v) <&!> (L <@> lambda(v) -> v * v * v * v), (+))
let checksum = s1 + 2.0 * s2 + 3.0 * s3 + 4.0 * s4
```

(examples/03 (b), the fused-pool shape.) Probed: one loop over x with four accumulators,
no pool allocated. **mt8 (settled by blade-B):** there is NO licensed parallel spelling of
a `<&!>` join — `omp` on a leg is emitted serial with the census note "fold accumulates into
a shared scalar, which is not race-safe" (`src/CodeGenFusion.fs:263`,
`src/CodeGenLoopNest.fs:1505`), and `omp` on the join's fold kernel is dropped silently (a
finding against the "dropped omp is never silent" invariant). A tuple-state fold threads in
the C++ lane but the LLVM lane refuses it. Decision: keep the `<&!>` idiom; the mt8 row
reads **"no parallel form"**.

## Regimes

licensed and repro (differing in the LLVM lane's `reassoc` legs only; the C++ lane's join
is serial in both), fastmath; mt8 is reported as "no parallel form" for Blade (Fortran arms
run it).

## Hypothesis

- vs natural (four-pass baseline): **WIN 2–3x** (one DRAM pass vs four).
- vs expert, `repro`: **equivalent** (same loop, four serial chains each).
- vs expert, `licensed`: blade-llvm **equivalent** (both vectorize the four accumulators);
  blade-cpp **detectable loss to loss** (four serial chains against the expert's `simd
  reduction`; the 40 MB stream limits the gap).
- mt8: if no parallel form exists, the Blade cell is "no parallel form" and the expert's
  8-thread reduction wins outright — reported as a gap, not hidden.

## Risks

- The fuse gate must accept four legs; `blade plan` must show one traversal. If it splits,
  the result reverts to natural's.
- Powers: every arm must spell x⁴ as `((x·x)·x)·x` (no `x**4` → `pow`, no
  `(x²)²`); the Fortran agent checks that `x**4` under `-O3` is expanded as repeated
  multiplication in that order (it is usually `(x²)²`, which differs in rounding — if so,
  write the products explicitly in both tiers).
