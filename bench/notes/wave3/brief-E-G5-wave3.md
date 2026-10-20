# Wave 3, item 1 -- E-G5 `let rec` step threading (recur mt8): the build brief

Base: `worktree-bench-fortran` at ccde7e4e (the wave-2 tip f1910060 plus notes). Design input:
`round2/brief-E-parallel-permission.md` section 3 and `round2/codex-review-DE.md` objections 14-16.
This note records what wave 3 MEASURED before coding and the form it builds. Everything below
that is not marked MEASURED is a design, not a result.

## 1. The two measurements that fix the form (MEASURED 2026-10-20, this box, ucrt64 g++ 16, libgomp)

`forkcost.cpp` (scratch): 300 steps over 20011 cells, body `b[i] = a[i] * 1.0001 + 0.5`,
`OMP_NUM_THREADS=8`, the harness's mt8 environment (unbound, `OMP_DYNAMIC=FALSE`, no wait policy
-- the harness strips every `OMP_*`/`GOMP_*` knob it does not set):

| schedule | per step |
|---|---|
| `#pragma omp parallel for` per step (brief E "tier 1") | 47-51 us |
| one region, `omp for` + `omp single` per step (two barriers) | 90-97 us |
| one region, explicit ownership, one `barrier` + `single nowait` running a 20011-cell serial fold | 37-46 us |

`OMP_WAIT_POLICY=active`, `GOMP_SPINCOUNT=infinite`, `OMP_PROC_BIND=true` change nothing
(47 / 94 / 46 us). The serial step itself is ~5 us. So on this box a team entry or a barrier costs
~40 us: 300 of them are 12 ms, against an expert mt8 arm of 7.1 ms (gfortran) / 8.0 ms (ifx).
Brief E's tier 1 (per-step team) and the "barrier + one ascending row fold" repro schedule are
both DEAD on arrival here. The only competitive form is the expert's own: ONE region, contiguous
x ownership fixed for the whole time loop, NO per-step synchronization.

The current emission (`blade emit` of `bench/kernels/recur/recur.blade`, licensed): the time loop
is `for n in 1..301 { lag view BUF(n-1); for k in 0..20011 { 8 scalar lets; BUF[n][k] = v };
row view BUF(n); p = blade_fold::sq<double, 8>(row); acc = acc + p }` (slice-write fusion +
row-fold fusion, both applied). With the proposed `where omp(v: 1)` on the step's final map,
slice-write fusion DECLINES today (`an elementwise kernel is not a plain pure scalar kernel`:
`IRMono.tilePlainKernel` rejects `IsOmpParallel`), `step(...)` is called per step, its map runs
as a `BLADE_OMP_PARALLEL_FOR_SIMD` flat sweep (one team per step -- the 47 us case) and the row is
copied: Codex objection 14's "baseline pollution" confirmed in the emitted code.

## 2. The transformation

Source spelling (candidate (a) of brief E, unchanged): `where omp(v: 1)` on the inductive arm's
FINAL spatial map. recur.blade's `step` keeps its four named stages and replaces only the last
expression by `method_for(zip(u, k1, k2, k3, k4)) <@> lambda(v, a1, a2, a3, a4) where omp(v: 1)
-> ... |> compute`.

Admission (pre-split, `Optimize.fuseSliceWritesModule`, gate `BLADE_REC_STEP_OMP` in
`optimizerGates`, default on): the cell expander admits a kernel that fails `tilePlainKernel`
ONLY by carrying an `omp` request, and only at the ROOT of the slice (the slice's own node, or
the final node of an inlined callee body after its lets) -- a nested stage map with a request
still declines as today (no scatter of licences, no union). The request is a map licence over
the x axis of a rank-1 row, so it names the cell loop. The pass records a `RecStepTable` entry
(keyed by the cell loop's var and by the time loop's var, the `CellFoldTable` pattern) iff:

- the store's leading index is the enclosing time loop's variable `n` and the loop starts at
  `lo` (read off the enclosing `IRForRange`, threaded through `rw`);
- every row view of the buffer the slice reads (the `outsideOk` scope check, which only ever
  admits `BUF(idx)` row views) has `idx = n - c` with a literal `c >= 1`, or a literal row
  `r < lo`. Reads are at the cell being written (the expander indexes every row view at `k`),
  so each worker reads only its own x of strictly earlier rows: same-x, earlier-row -- the
  proof the barrier-free schedule needs. The lag-2 shape (`prefix(n - 2)`) is a guarded
  `IRIf` between whole rows today and declines slice-write fusion itself; v1 does not reach it;
- `Optimize.scheduleMayWriteIn programFuncs modul` is false for the slice and for the whole
  time-loop body (producer, intervening bindings); the expander already requires capture-free
  pure total kernels and refuses calls, matches and loops (every private is a scalar let).

`fuseRowFoldsModule` (runs after) attaches the fold facts to the entry when it fuses the row
fold: the row-view let, the fold let `p`, the accumulator, the row kernel's cell term
(`Square` for `prodsum(row, row)`, else `None`), after its own five-party write checks (unchanged).
The B2r `CellFoldTable` entry is NOT recorded for a threaded step (the chain cannot advance per
cell across workers); the decision record says so.

Emission (C++ lane only; `CodeGenBinding.genForRangeBinding` on the TIME loop, when
`ompThreadEmissionEnabled ()`), fail-closed on shape: the time loop's let chain must be exactly
`[lag row views (BUF(n - c) / BUF(r)); the cell loop; (row view; p; acc <- F(acc, p))?]` with a
unit tail; anything else declines with the census marker and the plain loop.

```
{   // rec-step-parallel v1
    const size_t __rsn = E;  int __rsT = blade_omp_get_max_threads(); clamp to [1, E];
    [licensed+Square] double* __rspart = new double[N * __rsT](); int __rsnt = 1;
    BLADE_OMP_PARALLEL_NUM_THREADS(__rsT)
    {   const int __rnt, __rt; const size_t __rlo = blade_fold::chunk_lo(__rsn, __rt, __rnt), __rhi = ...;
        [licensed+Square] if (__rt == 0) __rsnt = __rnt;
        for (n = lo; n < N; n++) {
            <lag views, private>
            for (k = __rlo; k < __rhi; k++) { <cell lets>; BUF[n][k] = v; }      // same text as today, chunk bounds
            [licensed+Square] __rspart[n * __rsT + __rt] = __rhi > __rlo ? blade_fold::sq<double, 8>(BUF.data[n] + __rlo, __rhi - __rlo) : 0.0;
        }
    }
    [licensed+Square]  for (n = lo; n < N; n++) { p = +0 then += part[n * T + t] for t < __rsnt ascending; acc = F(acc, p) }
    [otherwise, fold present]  double* __rsslot = new double[N];
                 BLADE_OMP_PARALLEL_FOR over n in lo..N { row = BUF(n); __rsslot[n] = K(row) }   // K's own serial text, one COMPLETE row per slot
                 for (n = lo; n < N; n++) { acc = F(acc, __rsslot[n]) }                         // the original time chain, in order
}
```

Legality: the region writes each cell exactly once by its owner; a worker reads only its own
x of rows `n - c` (its own earlier stores, program order) or a row below `lo` written before
the region; the seed row's fold (row 0, `pre`) stays before the region as today. Licensed +
`prodsum(row, row)`: the row chain is a builtin `+` (the same licence the K-lane `sq` already
spends); per-worker partials over `chunk_lo` chunks combined ascending from +0 is the Path B
discipline -- deterministic for a fixed team, no bit contract (none exists in the licensed
build). Every other case -- `BLADE_FP_REPRO=1`, `where repro`, a row kernel that is not the
`Square` term -- keeps EVERY chain complete: slot n is K applied to the complete row n with
K's own serial text (strict, G35's row-slot discipline), and the time chain `F(acc, slot[n])`
runs ascending from the original seed. Byte-identical to the serial program under
`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` for every team size (the raw-bit block pins T = 1, 3,
8 against the `BLADE_OMP_THREADS=1` build). The strict post-pass re-reads the trajectory
(48 MB at the bench shape) -- the cost of strictness; brief E's alternative (barrier + one
serial fold per step) is 300 barriers, measured above.

Not threaded (census line, plain loop): threads suppressed (`BLADE_OMP_THREADS=1`: the
`[omp] requested but emitted serial: BLADE_OMP_THREADS=...` marker at the time loop, since the
request was inlined away with the map); the LLVM lane (sequential IR; its census names the
inlined request's kernel); an emission-time shape mismatch. Knob off (`BLADE_REC_STEP_OMP=0`):
the expander declines the requesting root map exactly as today (the per-step flat sweep and the
copy), recorded `declined -- disabled by BLADE_REC_STEP_OMP=0`: that is the A/B's knob-off
mirror, and also the "OLD binary on the new source" arm.

Interpreter: the IR is unchanged (side table only). LLVM: unchanged emission.

## 3. Tests

- corpus `recursive-arrays/031` (stored trajectory + total; licensed step map; EXPECT pins
  exact), `032` (the fold consumer, `prodsum(row, row)`, pins exact dyadic), `033` (declines:
  a requesting NESTED stage map; a non-Square row kernel -> slot form; a `while`-guarded
  recursion), `mutability-errors/` a captured write inside the requesting map (BL4005).
- `tests/OptimizeTests.fs`: decision + emission needles (region, `chunk_lo`, `__rspart`,
  `__rsslot`; threads-off marker; knob-off decline + `step(` call restored).
- raw-bit block in `tests/CellEpilogueTests.fs` (same machinery): a recur-shaped program at
  n = 2003 x 11 rows, T = 1 / 3 / 8 vs the threads-off build: repro + contract off bitwise;
  licensed within 1e-12 and the trajectory pool bit-identical (the cells are the same scalar
  chain whoever owns them).
- `blade test optimize`, `recursive-arrays`, `cell-epilogue`, `omp-pragma`, `interp
  recursive-arrays`, `opt-diff recursive-arrays`; the five wave-end gates.

## 4. Bench

`recur.blade` adopts the spelling; `kernel.json no_parallel_form: false`, notes updated;
`spec.md` text. A/B mirrors (one root per binary): `w2` (the wave-2 binary, ORIGINAL source =
OLD), `w3` (this tip), `w3a` (`BLADE_REC_STEP_OMP=0`: the new source on the new binary with the
admission off = per-step teams + copies, Codex 14's polluted baseline), `w2n` (the OLD binary on
the NEW source) only if cheap. Cells: recur serial licensed / repro (controls: the serial
emission must be byte-identical to w2's -- the region is threads-only), recur mt8 licensed /
repro (the new arms). DROP rule (brief E): if the mt8 arm does not beat NEW's own serial arm
beyond the CI, the default stays off and the form is recorded as measured-negative.
