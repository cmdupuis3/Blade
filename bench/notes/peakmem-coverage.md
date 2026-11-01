# Peak-memory coverage census (round 4 wave 3, item E1)

Base: master `44c6d570`. Every status below was checked on that tree: a `blade plan` record (the
binary built from the base, `bin/Release/net10.0/Blade.exe`), an emitted-allocation grep
(`blade emit`), or a test block that is green in the base validation run
(`C:\bt_drf\results\20261031-092537-codex-r4m.txt`: full 13040/0/5). Measured numbers are rows of
`bench/results/stats.csv` (the harness's peak working set, `peak_ws_mb`, full size unless noted;
`-inputonly` = the kernel's input-only twin, i.e. the floor); nothing new was timed for this census.
"Unmeasured" means no harness row exists for the storage target -- the mechanism may still be built.

Status vocabulary: COVERED = the family's storage target is met by a built default (or a named
opt-in arm, said so) with a witness; PARTIAL = part of the target is met, the uncovered shape is a
gap row with a witness program; NOT COVERED = nothing built.

| Family | Status | Mechanism (plan rule / knob) | Witness | Measured |
|---|---|---|---|---|
| PM08 allocation churn / retention | COVERED as an opt-in arm; the plan's churn sweep is unmeasured | runtime pool cache (16 KiB-4 MiB blocks, 8 MiB per thread, default) and `BLADE_POOL_RETAIN` (`1` = 64/96 MB, `<max>:<budget>`); `blade plan` line `build: pool retention: ...`; run record `policy.pool` | `blade test alloc` (layout binary under default, `1`, `32:64`), `run-record`, `cli`, `llvm shim` (G52) | recur mt8 licensed 9.13 -> 6.76 ms, peak 51.7 -> 51.8 MB; stencil mt8 repro 8.43 -> 7.80 ms, peak 51.4 -> 51.6 MB (labels `r3w2` / `r3w2retain`): retention relocates the release, it does not lower the high water. The plan's below/inside/above-band, tiny/large alternation sweep was never run |
| PM11 multiple statistics | PARTIAL -> **G107** | sibling-fold traversal sharing (`[sibling-folds v1]`, `BLADE_SIBLING_FOLDS`), explicit `<&!>` reduction joins, threaded join (`BLADE_JOIN_THREADS`), producer-input legs (`BLADE_EPILOGUE_LEGS`, kept inline opt-in) | corpus `sibling-folds/001` (3 folds, one traversal, applied), `019` (input legs; the kept inline declines by default) | onepass `old`/`new` (one-traversal joins): peak 43.4 MB both, input-only 43.3 MB -- the target (no extra state) holds where the join applies. Uncovered shapes (G107) unmeasured |
| PM17 stable materialized selection | COVERED | compaction fusion v2 (`[compaction-fusion v2]`, `BLADE_COMPACTION_FUSION`): count in the mask pass, ONE branchless gather into `m + 1` cells (exact-size, not `n + 1`), positions elided when the demand census allows; `gather` = the opt-in n + 1 single-pass arm | corpus `index-types/522` (positions elided), `524` / `529` (positions demanded keep them), `527` (run-time extent) | maskagg serial licensed blade-cpp peak 21.6 -> 17.8 MB (`r3old` -> `r3new`; input-only 13.0); the `gather` arm 17.8 MB (`r3gather`). The remaining 4.8 MB above input-only is the stored Bool `keep` and the required compact output |
| PM18 shared selection across columns | COVERED | T4c shared selection (`one selection index shared by J companions`), joint gather under `gather` (J <= 4) | corpus `index-types/532`, `541` (two companions, one index), `533`-`536` (keyed read, other index space, keep read / writer between: declines) | unmeasured as memory (G70: directional whole-program A/B only, filtergroup J = 3 0.70-0.76) |
| PM20 bounded-key group aggregates | PARTIAL -> existing **G71** | aggregate-only grouping (`[group-aggregate v1]`, `BLADE_GROUP_AGG`), positional + EnumIdx regimes | corpus `sql-group-by/065`, `067`, `071`, `073`, `074` applied; `069` (licensed Float64 prodsum) and `072` (top-level grouping, row read, `where omp` peel) decline | groupagg full: repro blade-cpp 35.5 -> 20.6 MB (`r3w3t3off` -> `r3w3`, input-only 20.6 = target met); the LICENSED default stays 35.5 MB (declined, strict-first gate; `licensed` opt-in arm 20.6 MB, `r3w3t3lic`). Uncovered: licensed Float64 folds in the default build, top-level groupings (G71's text) |
| PM21 dynamic group aggregates | PARTIAL -> existing **G71** | same pass, dynamic regime (C-core key -> group map, first-occurrence numbering) | corpus `sql-group-by/064`, `068`, `070` applied | groupagg_dyn full: repro blade-cpp 35.6 -> 20.6 MB, blade-llvm 35.1 -> 19.9 MB; licensed default 35.6 MB (declined). The plan's G close to N hash-resize peak is unmeasured |
| PM22 membership working set | COVERED (mechanism); unmeasured | membership hoisting (`[member-hoist v1]`, `BLADE_MEMBER_HOIST` = chooser / `linear` / `bitmap` / `hash`), one structure per host, built before and freed after it | corpus `sql-unique-contains/011` (linear), `012` (bitmap), `013` (hash, extreme keys), `014`, `019`, `020`, `022` | no harness peak row (G65: one directional `blade run` A/B). The plan's per-representation resident-set comparison is unmeasured |
| PM24 materialized join | COVERED in C++; LLVM declines with a record (G72) | hash equijoin (`[hash-join v1]`, `BLADE_HASH_JOIN`): stable CSR, counting pass sizes J exactly before any store, cardinality-only index | corpus `sql-joins/001`-`019` (`006`, `007`, `013`-`019` decline) | hashjoin PILOT: blade-cpp peak 25.4 -> 5.4 MB (`r3w3t2off` -> `r3w3`; input-only 5.3). Full size and the LLVM route are G72's open follow-ups |
| PM30 gram operator actions | COVERED in C++; the LLVM lane REFUSES (recorded, C++ fallback) | the `gram_apply(A, B, x)` node: two rank-1 temporaries (t over K, y over R), never the R x R pool; `[gram-apply-advisory v1]` names the materialize-then-apply spelling (advise, never rewrite) | corpus `math/077`, `078`, `ad-jvp-comb/109`; emission of a 1201 x 401 probe allocates A, v, `y__t` (401) and `y` (1201) only, native and BLAS routes alike; `BLADE_LLVM=1 blade plan`: "the IR node IRGramApply in array position -- no arm in the llvm lane" | unmeasured |
| PM32 factor once, stream RHS | PARTIAL -> **G109** | `m.lu(A)` returns the factor as a value; `m.lu_solve` / `lu_solve_t` apply it with no refactoring | corpus `math/083` (four solves, one factor, bitwise `m.solve`); the NATIVE emission (`BLADE_BLAS=off`) writes only the solution vector per RHS | unmeasured. The LAPACK route (default whenever `OPENBLAS_DIR` is set) copies the whole factor per solve (G109); the LLVM lane refuses `IRLu` ("in pack position") |
| PM48 immutable revision reuse | COVERED as an opt-in arm (C++, Icechunk); footprint unmeasured | revision reuse (`BLADE_TILE_CACHE`): per-tile content keys, cold/warm/changed-tile probes, truncation heal, concurrent stores | `test icechunk` section "revision reuse: tiles across snapshots": E@s2 after E@s1 recomputes ONLY the tile holding the changed chunk (1 of 2) and reads only its 2 chunks; an unobserved input fetches nothing for hit tiles | unmeasured (no disk-footprint or in-memory bound report beyond the `[tiles]` census) |
| ST01 stored input -> private maps -> seeded fold | PARTIAL -> **G108** | compute elision (`BLADE_COMPUTE_ELIDE`) + elementwise fusion; the wave-1 C01 selected terminal (`[selected-terminal v1]`, a mask + compound + one seeded fold -> one guarded scan, REPRO builds only) | `blade plan` of the G108 witness: `chain.q` applied, `chain.p` declined ("reads `p`'s cell 2 times"); `selected-terminal` applied only under `BLADE_FP_REPRO=1` ("requires the reproducible serial fold graph") | unmeasured; the emitted residue is one N-cell Float64 pool (8.0 MB at N = 1,000,003) per uncovered spelling |
| ST02 rank-1 Zarr stream -> maps -> sum/sumsq/count | PARTIAL -> **G107** (siblings) / **G108** (route scope) | the wave-1 D01 block consumer (`rank1-zarr-joined-scalars`, `CodeGenBinding.tryGenRank1StreamJoin`): one shared producer, two seeded addition chains, one arithmetic block loop | `test zarr` "rank1 Zarr block consumer: shared producer and seeded scalar join" (route marker asserted); "streamed fold siblings: not joined" (the sibling decline) | unmeasured. The route refuses unless EXACTLY two seeded Float64 `+` legs with literal seeds AND `BLADE_FP_REPRO=1` ("a licensed fold requires BLADE_FP_REPRO=1 for the scalar block route"); no count leg; separate statements over a streamed source are not joined |

## Gap rows opened by this census

The three rows are appended to `bench/notes/gap-register.md` with their witness programs.

- **G107** (PM11, ST02): sibling-fold coverage holes -- an UNSEEDED leg with a non-builtin kernel
  (min/max) is not a candidate and its exclusion leaves no record; a mixed licensed/unlicensed
  group in the default build declines the WHOLE join (4 statistics -> 4 passes); separate
  top-level `let` folds over one array are never siblings; streamed operands decline.
- **G108** (ST01, ST02): chain-to-fold intermediates still stored -- compute elision declines a
  producer whose consumer kernel names its parameter twice (`v * v`); a top-level fold over a
  two-level deferred chain materializes the inner map; the C01 selected terminal and the D01
  stream block consumer are repro-only.
- **G109** (PM32): the LAPACK `lu_solve` adapter copies the n x n factor into a column-major
  scratch on every right-hand side.

## Not re-verified here

Wall-time claims are the cited A/B labels' (indicative medians, see `stats.csv`). The plan's
sweep axes (PM08 band edges, PM11 q = 16, PM17 six selectivities, PM21 G close to N, PM22 span /
cardinality) were not run by anyone; they are measurement work, not missing mechanisms, and are
listed in the Measured column rather than as gap rows.
