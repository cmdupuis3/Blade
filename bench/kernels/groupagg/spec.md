# groupagg — aggregate-only GROUP BY over positional keys (round 3, W3-1)

Group B. Directory `bench/kernels/groupagg/`; the dynamic-key variant is
`bench/kernels/groupagg_dyn/` (same definition, keys and group order below).

## Object and operation

n rows, a key per row and a value per row:

    key(k) = floor(ngroups · u(k)),   u(k) = gen(k, 10000019, 0, 1)      key ∈ [0, ngroups)
    x(k)   = gen(k, 20000038, 0, 2)

The **named outputs** (stored in every arm) are the three per-group arrays over the groups
g = 0 .. ngroups−1 (positional: group g IS key value g, empty groups included):

    s(g) = Σ_{k : key(k) = g} x(k)          (Blade: reduce(r, (+)), unseeded)
    q(g) = Σ_{k : key(k) = g} x(k)²         (Blade: prodsum(r, r))
    c(g) = #{k : key(k) = g}                 (Blade: extents(gk))

and

    checksum       = Σ_g s(g)² + Σ_g q(g) + Σ_g c(g)²     (the last an exact integer, added as a float)
    input_checksum = Σ_k x(k) + Σ_k key(k)

`Σ s²` and `Σ c²` depend on the grouping (which rows land in which group), unlike `Σ s` and
`Σ q`, so the checksum is sensitive to the key map; the per-group values themselves are
checked by the dump variant at smoke size (`-DBENCH_DUMP`: s, q, c per group).

## Chain order (the strict association)

Every per-group chain runs over its group's rows in **ascending row order**, from 0
(`0 + x = x` exactly since x ≥ 0, so this equals Blade's unseeded fold, which copies the
first value, and its prodsum, which starts at 0 and adds the rounded product). The three
group folds then run in group order g = 0, 1, ..., and `checksum = (Σ s² + Σ q) + Σ c²`
left to right; `input_checksum = (Σ x) + (Σ key)`, each ascending. This is the order
`BLADE_FP_REPRO=1` evaluates in both Blade lanes and the order every expert loop runs; the
pins (`validation/pins-B.json`, from `validation/ref_groupagg.py`) are this ORDERED SCALAR
ORACLE, not the correctly rounded sums (printed beside them; they agree to ~1e-16).

## Inputs

| array | shape | j | seed | lo | hi | note |
|---|---|---|---|---|---|---|
| key | (n) | 1 | 10000019 | 0.0 | 1.0 | then `floor(ngroups · u)`; `ngroups · u` is exact (31-bit fraction × 11-bit integer) |
| x | (n) | 2 | 20000038 | 0.0 | 2.0 | |

## Sizes

| variant | n | ngroups | inputs | state | est. serial |
|---|---|---|---|---|---|
| smoke | 30011 | 1009 | 480 KB | ~24 KB | < 1 ms |
| full | 1,000,003 | 1009 | 16 MB | ~24 KB | 8–20 ms (Blade) |

No extent is a power of two. Input share ≈ 45% (the generator runs twice over n) → an
`inputonly` twin per arm is REQUIRED.

## Tiers

- **natural**: the literal transcription with masked intrinsics, one per group and output:
  `s(g) = SUM(x, MASK = key == g)`, `q(g) = SUM(x*x, MASK = key == g)`, `c(g) = COUNT(key
  == g)` — O(n · ngroups), ~3e9 element tests at full size (seconds). It is the dense
  baseline, not a competent program.
- **expert**: ONE pass over the rows in source order into direct-indexed accumulators
  s, q, c (ngroups cells each, L1/L2-resident), then the three group folds. The chains are
  the strict association above, bit for bit. Under `BENCH_LICENSED`, `!$omp simd
  reduction` on the group folds and the input sums only.
- **No mt8 arm in any language.** The per-group chains are independent, so a threaded form
  exists in principle (privatized per-thread accumulators combined in a fixed order), but
  that is a different association of every group's sum, and Blade v1 has no thread form.

## Blade idiom

```blade sketch
function kernel(key: Array<G like Ix>, x: Array<Float64 like Ix>) -> Float64 = {
    let gk = group_keys(key)
    let g = group_by(x, gk)              // a compiler-visible temporary of the function
    let s = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute
    let q = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute
    let c = extents(gk)
    prodsum(s, s) + reduce(q, (+)) + Float64(prodsum(c, c))
}
```

The grouping lives inside a function: a TOP-LEVEL grouped array is a named binding and keeps
its rows stored (round-3 review B/10), while inside a function `g` is a temporary whose rows
the aggregate-only plan may leave ungathered. `s`, `q`, `c` are named, stored arrays.

## Expected emission and regimes

- **repro** (`BLADE_FP_REPRO=1`): the aggregate-only grouping (`Blade.Optimize.planGroupAggregate`,
  rule `group-aggregate v1`, gap G71) runs ONE pass over the rows in source order — the
  key guard, `acc_s = first value, then acc_s + x` and `acc_q = 0 + x·x` per row, the
  count — with no permutation and no gathered pool; the peels read the states. Both lanes.
  Expected: parity-ish with the expert (both one pass; Blade adds the BL8006 key guard).
- **licensed** (default): the per-group folds are licensed builtin `+` folds whose current
  form is a per-group K-lane tree; the one pass's ascending chain would be a different
  association, so the plan DECLINES and the CSR (two key passes + a scattered permutation),
  the gather and the K-lane peels run. Expected: a loss to the expert. `BLADE_GROUP_AGG=licensed`
  takes the ascending chain (an association change, bits may differ; not the default).

## Hypothesis

repro: Blade ≈ expert (one sweep each over 16 MB of inputs, ~24 KB of state); vs natural a
large win. licensed: Blade loses to the expert by the CSR + gather + peel passes (today ~64
MB of logical traffic in 6 sweeps against the expert's 16 MB in 1).

## Risks

- The positional key guard (`key >= ngroups` aborts BL8006) is a compare per row the expert
  does not make.
- ngroups = 1009 keeps the state in L1/L2; a large ngroups (e.g. 100,003) would make both
  one-pass forms scatter-bound — not measured here.
