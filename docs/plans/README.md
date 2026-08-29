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
| [plan-graphs-trees.md](plan-graphs-trees.md) | DESIGN REFRESHED; P0 next | `TreeIdx<shape>` static trees: flat preorder storage on the ragged CSR shape, one path-domain slot (the SparseIdx escape hatch), derived dense axes. Graphs need NO new index type — `Trace`/`DAGIdx` deleted in the design refresh (`docs/features/graphs-trees.md`); walks are `let rec`, collapse is MonadPlus, acyclicity is a checked `where acyclic(g)` license |
| [plan-equivariant-nn-notebooks.md](plan-equivariant-nn-notebooks.md) | ALL BUILT; CONDENSED 2026-08-28 | Four shipping notebooks: NB1a Tetris deduction (chirality as a compile error), NB1b MD17-aspirin benchmark (full-stack training, live `plot.stream` loss, forces = −∂E/∂r transforming at 1e-15), NB3 matched-moment discriminator (rung staircase 100/50/157/198 hits, on prediction), NB4 COD crystals (Neumann whitening, exact bond-order classification on real structures). NB2 pair-cloud moment jets REMOVED in the condensing pass — decisive negative after the E1–E4 campaign; §3 keeps the full record. Plus the display-stream infra (sink, `emit_id`, `plot.stream`, notebook renderer); 71-entry gap census; #40/#44 fixed; idiom-audited |
| [plan-subset-split.md](plan-subset-split.md) | PLANNED: P0 sized, nothing built | `subset(A, d, lo..hi)` + `split` (a two-subset desugar) — the missing inverse of `join` (formalism.md:157, BL2001-unbound today). Revives the dormant `IRSubset` node; P1 makes it grad-admissible (scatter-at-offset adjoint via the existing accum lane), collapsing the notebooks' hand-enumerated weight-slicer literals. ~20 mirrored join-twin arms, zero fsproj churn |
| [plan-icechunk-provider.md](plan-icechunk-provider.md) | P0–P3 + demo notebook LANDED, hardened after an adversarial review; P4 open | Icechunk (versioned Zarr) as a fourth provider: `repo.checkout("ref"[, ic.tag])` factory dispatching on ref-unit markers, desugared to a canonical path key in one raw-AST pass; all ref→snapshot→chunk resolution at compile time (baked chunk tables, runtime stays pure C++17); axis identity — dense axes and packed pools alike — is shared across checkouts iff extent and content fingerprint are unchanged, survives type aliases, and names the axis and its split reason when it refuses. FlatBuffers/zstd DECIDED (vendored `flatc` accessors + ZstdSharp.Port); resolution failures surface at `blade check` as BL2008; open: P4 packed/orbit/window parity |

Conventions: status changes edit the doc's header and this table — never move or
rename the file. Docs for landed work may be deleted in bulk sweeps, but sweep
their citations in the same change (the ef94c50 deletion did not, and code
comments still cite several dead `docs/plan-*.md` paths).
