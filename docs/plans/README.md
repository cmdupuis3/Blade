# Living design docs

Working documents for features under design, in flight, or investigated and
concluded. Each doc carries its own `Status:` header — that header is the source
of truth; this table is the index. Files keep stable paths for their whole life
(verdict docs stay citable; see `plan-static-array-erasure.md`, deliberately
re-added after a mass deletion left dangling references).

| Document | Status | Topic |
|----------|--------|-------|
| [plan-ad-combinators.md](plan-ad-combinators.md) | ACTIVE (F4 spec) | Per-combinator forward/reverse AD rules; the companion spec `plan-forward-mode-ad.md` §4 F4 deferred to |
| [plan-simplex-blocked-compute.md](plan-simplex-blocked-compute.md) | P0+P1 LANDED (rank 2); P1 GATE FAILED | Reusing Zarr SimplexBlocks math on the compute side: dense bricks + 2^-d residue. Correct and measured; S0 costs ~7% and wins nothing — S1/mirror modes untouched |
| [plan-toolchain-packaging.md](plan-toolchain-packaging.md) | LANDED (doctor/setup phases open) | deps.json pins, `blade doctor`, `blade setup`, toolchain.json |
| [plan-forward-mode-ad.md](plan-forward-mode-ad.md) | EXECUTED through F3 | `ad.jvp` forward mode, HVP, second order |
| [plan-static-array-erasure.md](plan-static-array-erasure.md) | VERDICT: REFUTED | Measured case against static-extent array erasure — do not build |
| [plan-llvm-backend.md](plan-llvm-backend.md) | IMPLEMENTED through M5, measured | `BLADE_LLVM` lane: emits, refuses whole-program, `blade test llvm` / `llvm-bench`. Codegen 4.5x faster than g++; runtime at parity — the fact-emission thesis stays refuted, R6 (toolchain) is what pays |
| [plan-mlir-backend.md](plan-mlir-backend.md) | PROPOSAL: unscheduled | If-we-did-it MLIR architecture sketch (cuda-tile target) |

Conventions: status changes edit the doc's header and this table — never move or
rename the file. Docs for landed work may be deleted in bulk sweeps, but sweep
their citations in the same change (the ef94c50 deletion did not, and code
comments still cite several dead `docs/plan-*.md` paths).
