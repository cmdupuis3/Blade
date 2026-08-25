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
| [plan-unroll-and-jam.md](plan-unroll-and-jam.md) | SPECIFIED: nothing built | Jam the enclosing OUTPUT axis around an in-nest fold: R independent chains without changing any cell's summation order, so BITWISE and licence-free where the licensed fold split measures 1.00x. Expect 3.4-4.3x in Blade's real shape (not the prototype's 13.95x). Two agents disagreed on tile width; §1 settles it — R<=4 AND explicit contraction suppression, since gcc contracts at >=8 accumulators under Blade's shipping `-ffp-contract=fast` |
| [plan-rank-r-former.md](plan-rank-r-former.md) | DESIGN: nothing built | BLAS-class rank-r symmetric contraction (`sum_t A(t,i1)...A(t,ir)`) with NO decompaction. The KRS schedule uses the packed pool's affine last axis to reduce rank-r to a ragged rank-2 gemm, claiming 100% of r! in flops where BCSS got storage only; bricks and S1 are NOT needed, and the real bottleneck is a dependent FMA chain. P0 is a census that can kill it |
| [plan-toolchain-packaging.md](plan-toolchain-packaging.md) | LANDED (doctor/setup phases open) | deps.json pins, `blade doctor`, `blade setup`, toolchain.json |
| [plan-forward-mode-ad.md](plan-forward-mode-ad.md) | EXECUTED through F3 | `ad.jvp` forward mode, HVP, second order |
| [plan-static-array-erasure.md](plan-static-array-erasure.md) | VERDICT: REFUTED | Measured case against static-extent array erasure — do not build |
| [plan-llvm-backend.md](plan-llvm-backend.md) | IMPLEMENTED through M5, measured | `BLADE_LLVM` lane: emits, refuses whole-program, `blade test llvm` / `llvm-bench`. Codegen 4.5x faster than g++; runtime at parity — the fact-emission thesis stays refuted, R6 (toolchain) is what pays |
| [plan-mlir-backend.md](plan-mlir-backend.md) | PROPOSAL: unscheduled | If-we-did-it MLIR architecture sketch (cuda-tile target) |
| [plan-llvm-runtime-shapes.md](plan-llvm-runtime-shapes.md) | RAGGED LANDED; group_by open | LLVM lane emits ragged/grouped shapes natively (RaggedIdx, EnumIdx/group_by): static ragged first (fully compile-time), then operand-valued extents, then CSR group_by — pure `.ll` for annotated key regimes, shim hash only for dynamic discovery |
| [plan-icechunk-provider.md](plan-icechunk-provider.md) | ACTIVE: P0 + surface in flight | Icechunk (versioned Zarr) as a fourth provider: `repo.checkout("ref"[, ic.tag])` factory dispatching on ref-unit markers, desugared to a canonical path key in one raw-AST pass; all ref→snapshot→chunk resolution at compile time (baked chunk tables, runtime stays pure C++17); axis identity shared across checkouts iff extent + coordinate chunk-refs are unchanged. FlatBuffers/zstd dependency choice deferred, gates P1 payload decode |

Conventions: status changes edit the doc's header and this table — never move or
rename the file. Docs for landed work may be deleted in bulk sweeps, but sweep
their citations in the same change (the ef94c50 deletion did not, and code
comments still cite several dead `docs/plan-*.md` paths).
