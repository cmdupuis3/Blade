# Blade examples

Five self-contained programs, each solving a realistic problem with the
idioms that fit it. They are a tour of the language's surface at working
scale (~150-250 lines each), not feature probes — for those see
`tests/corpus/`.

Run any of them from the repo root (needs g++ on PATH):

```
dotnet run -- run examples/01_weather_stations.blade
dotnet run -- check examples/01_weather_stations.blade   # typecheck only
```

Each file keeps the corpus conventions — a unique `// TEST:` first line and
`// EXPECT: name = value` comments documenting the verified output — so any
of them can be promoted into a suite category unchanged. Expected values
were derived independently of the compiler (exact hand arithmetic, plus
PowerShell reference implementations for cumulants, quadrature, and the
Euler simulation), and several examples also pin internal cross-checks.

| File | Problem | Idioms on display |
| --- | --- | --- |
| `01_weather_stations.blade` | Sensor-network QC and aggregation | `mask`/`compound` WHERE, semijoin via `contains`, `group_by` + per-group `reduce`, foreign-key re-broadcast of computed aggregates, key-extractor `sort`, positional mask AND/OR, `intersect`/`union`, single-pass multi-statistic sweep (`<&!>`-fused `reduce`) |
| `02_portfolio_moments.blade` | Two-asset return analytics | `moments`/`comoments`/`cumulants` formers, the fused pool == `gram` == pooled-covariance three-way pin, `where comm` + `reynolds` packed symmetric storage, `decompact`, `independent()` + `where indep` licensed `Dist` algebra, `dist_affine` hedging pushforward |
| `03_signal_conditioning.blade` | Multi-channel DAQ conditioning | deferred `object_for` stages composed `>>@`, `@>>`, the duality identity, 3-way `<&!>` single-pass RMS/peak/mean, `<$>` dB post-map, `guard` gating, `<|>` failover, `object_for(<@>)`/`object_for(<&>)` sections with `|@>` chains, 3-way `zip`, `>>=`, `sequence`, `replicate`, `range`/`reverse` |
| `04_trajectory_sensitivity.blade` | Projectile with drag | unit-checked kinematics (derived `velocity`/`acceleration` units), `grad()` over a quadrature loop with in-file finite-difference cross-checks, and a gated for-in Euler recurrence — the imperative subset used exactly where it belongs |
| `05_streaming_telemetry.blade` | Distributed latency telemetry | `mstate` sufficient-statistic states, chained `mstate_merge` (monoid), merged == batch cumulant pin, `comoments_merge` pooled covariance, `dist_affine` KPI transform, `moments(d, k)` Wick/Isserlis reconstruction |
