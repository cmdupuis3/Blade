# C02 source and emission census

Runner: Luna/high. Read-only census on the frozen `c-terminals` C01 checkout. The C02 README SHA256 is `2D07D91828D9E754CF0BE6AB3F23864C302427456069AE25C55E2DB83822F1E0`; `probe-ir.fsx` SHA256 is `5A437280EC29A3C1CD14E5300E4802B2F362EA354CD5C5E59D46BC8C4F98794B`. Hashes for all eight fixture inputs are included below. Existing compiler identities were unchanged: `Blade.dll` `5666D72AEB1F6A445D4FAA9823B4B4EAA0B694A367F4EB34927D224CBA8472EA`; `Blade.exe` `00522C896F3B5D992E24D4BE54170E114D68E5A059C7D3D5B852D16E1826C622`.

For each fixture, the supplied FSI lowering/validation probe, `Blade.exe emit`, and `Blade.exe plan --json` all exited 0: **24/24 invocations passed, 0 refused**. The commands ran serially from the explicit C01 worktree root with TEMP/TMP in `bench/.build/C02-runner/tmp`; outputs and logs are absolute paths under `bench/.build/C02-runner/<case>/`. The FSI probe validates and prints IR; it does not call codegen or interpret the program. `emit` and `plan` did not compile or run generated programs. The targeted LLVM follow-up emitted case 03 with `BLADE_LLVM=1`; it exited 0 and produced a 5,811-byte LLVM module. No tests, C++ compilation, program execution, or benchmarks were run.

For cases 01 and 02, `M` is binding ID 4 and its declared axes are `R` ID 2 (extent 2) and `C` ID 3 (extent 3). Case 01's private `v12` / local ID 12 has `IRApplyCombinator` value and `IRCompute` body, typed `Array<Float64 like R,C>`. The binding is scoped by the let around the outer row apply and has exactly one consumer there: `APPLY inputRanks=[1] outputRank=0`, kernel `v21`, array `[v12]`. The row callable parameter is ID 13 (`__pfrow8`), and its `REDUCE` reads that row value with seed 7.0. The callable has no captures; C++ initializes each row accumulator to 7.0 before traversing the row. Case 02 has the same source axis IDs, private local ID 14, one row-apply consumer, row parameter ID 15, no captures, and seed 7.0; the row kernel is the ordered `(a * 2.0) + b`. These identities come from the IR records and references, not generated C++ variable names.

Current emissions for cases 01/02 allocate required `M` (six doubles) and a separate mapped rank-2 temporary (`__v12` / `__v14`, six doubles), fill the temporary from `M`, and read it during a row reduction into rank-1 `rows`. The temporary has one consumer and no callable capture. Cases 01/02 are the target shape for removing that private materialization while retaining `M` and row results.

Case 03 binds `IRReduceCompute` inside the row callable. Its C++ emission allocates only required rank-2 `M` and rank-1 `rows`; each row is mapped and reduced directly, with no extra mapped-matrix pool. The LLVM emission confirms the same shape: exactly two `blade_alloc_cells` calls, 6 cells for `M` and 2 for `rows`, no mapped producer allocation. `fold-form` plan entries decline a faster fold under the requested reproducible policy; the direct rowwise map/reduce route itself is accepted.

Cases 04/05 retain named `P` as a rank-2 allocation and fill. Case 04's computed `P` is subsequently sampled and is also printed by default; `rows` and `sample` print as well. Case 05's `P` is deferred in IR, but default auto-print forces it under its own name and preserves the full pool and `P = ...` print, in addition to `rows`. Case 06's already-covered two-vector route allocates two rank-1 inputs and rank-1 `rows`, with no rank-2 producer.

Both empty probes are accepted, not refused. Case 07 has `M` and a separate mapped temporary with shape `{2,0}`, then rank-1 `rows` of length 2; emitted code seeds two row accumulators with -7.25 before their zero-iteration inner folds. Case 08 has `M` and mapped temporary shape `{0,3}` plus `rows` length 0; the outer loop has zero iterations, so no row seed executes. These are source/emission observations only; no runtime outputs or seed counts were tested.

Full IR, C++, plans, stderr, command provenance, and the index-identity reflection dump are in [C02 runner artifacts](../../../../c-terminals/bench/.build/C02-runner). The eight fixture hashes:

| Input | SHA-256 |
|---|---|
| `01-primary.blade` | `39014067D5A0353FA669AA2E2F3332BEF1C61B313CAE435D7200B762E2A55744` |
| `02-ordered.blade` | `CCE42047AE6B1BB36770505BF8B84C5EF39DF85D74634DF876E8F6C3DBAB0325` |
| `03-rowwise.blade` | `3F84E8DAEAD3E033E97FBA9ABC7DDD8D619BDAE13637A24C9FB1D8A75C918A8C` |
| `04-named-computed.blade` | `CFA4286647774B0A063F934A0B4FB2463DAE59E6DE7960E5A55D299202A6200F` |
| `05-named-deferred.blade` | `4318A8FB2A819493B206A9974DFCAE57B75B47BAEAC4FC275B87298E6D68A550` |
| `06-two-vector-covered.blade` | `D2BDC97F071C21DAC23FA0AE8B2F59E75C7AD1C37A37F3FD262EC19B2E9C8F42` |
| `07-zero-columns.blade` | `8C496932DFA97894C59C27741360F43D578BEA7735EE4D4BE8F99610B0556019` |
| `08-zero-rows.blade` | `5A38D365DAD4330E979C77CF440F3CFA69D249AB6681ADD77C71FEC35E8D9F72` |
