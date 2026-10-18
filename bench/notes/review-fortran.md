# Fortran fairness review — dispositions

The independent Fortran reviewer (Codex, read-only, 2026-10-17) read all 23 Fortran sources,
the support module and wrappers, the specs, the Blade sources and the archived emissions. Its
verdict: the expert tiers are credible implementations, not yet publishable because two
findings gave Blade permissions Fortran was denied, and one changed the specified arithmetic
grouping. Full text: `review-fortran-codex.md` (verbatim). Dispositions by the orchestrator,
with owners. Owners record what they applied in their own notes.

| # | severity | finding | disposition | owner |
|---|---|---|---|---|
| F1 | blocker | comoment3 expert regroups the triple product as `Xi * (Xj * Xk)`; Blade (and the spec's left-to-right product) is `(Xi * Xj) * Xk` — one-ulp per cell, hidden by the tolerance | ACCEPT the reviewer's patch: pack the `(Xi * Xj)` panel once per i-panel and multiply against raw `X(k, s)` panels; keeps ascending accumulation, canonical grouping, and the microkernel. Spec: "multiply by 1/n" → "divide by n once per cell" (both tiers already divide); state the distinction between permitted permutation-rounding differences (dense natural evaluates permutations independently) and regrouping the canonical cell (not permitted). | FORT (patch), ARCH (spec) |
| F2 | blocker | maskagg spec claims licensed = repro; the archived licensed Blade emission has 8-lane accumulators for BOTH the output checksum and the input sum | ACCEPT: expert gets `BENCH_LICENSED` simd reductions on both sums (compaction stays sequential); spec and README matched list corrected: maskagg licensed ≠ repro. | FORT, ARCH |
| F3 | major | every expert's input-checksum fold stays serial while Blade's licensed emission reassociates them (8 lanes) in every kernel | ACCEPT: `BENCH_LICENSED` simd reductions on the input-checksum loops of fusedchain, onepass, dot (two sums), saxpy (two sums), stencil (per-row), recur; the matched-permission inventory explicitly includes input-checksum folds (grouping between separate inputs/rows kept). | FORT, ARCH |
| F4 | major | recur mt8 ends 601 worksharing constructs (a production loop and a checksum reduction per step) | ACCEPT: each thread owns a contiguous x-range for every time step inside one parallel region; per-thread checksum combined once at exit; per-cell arithmetic unchanged (thread-level regrouping of the checksum is already allowed in mt8 repro). | FORT |
| F5 | minor | gram / comoment3 / antisym (and expert_blas) leave the licensed output-checksum folds scalar | ACCEPT the simd-reduction patches under `BENCH_LICENSED`. | FORT |
| F6 | minor | gram expert's input checksum walks the transposed storage at a 12.8 KB stride | ACCEPT: sum mr adjacent variables together (each row still ascending in s); consider the same for antisym and comoment3 if cheap. | FORT |
| F7 | major | ifx fuses input generation + kernel + input sum in fusedchain expert (stores retained); README forbids fusing generation into the consumer | PROTOCOL REVISION: compiler-generated fusion is permitted for every language; the materialization requirement is that the inputs are STORED (the store audit checks it); the SOURCE may not fuse generation into the kernel. Record per arm, from the audit, whether the compiler fused. | ARCH |
| F8 | minor | dot natural's mt8 arm wraps `DOT_PRODUCT` in a workshare that runs serially, while onepass/fusedchain natural use explicit `parallel do` per pass | ACCEPT for consistency: mt8 natural dot = explicit `parallel do reduction(+:cs)` loop (spec allows); serial keeps the intrinsic. | FORT |
| F9 | note | per-kernel verdicts; no data race or missing firstprivate found; antisym natural: two-MATMUL alternative also natural | Note the two-MATMUL alternative in the antisym spec's selection record as an equally natural spelling not measured. | ARCH |
| F10 | note | keep the 8x6 microtiles pending measurement; no inner-loop integer division | No action. | — |
| F11 | minor | README names `-fno-external-blas64` too; wrapper passes only `-fno-external-blas` | ACCEPT: add `-fno-external-blas64` to build_gfortran.ps1 (and the harness flag table if it lists them). | FORT, HARN |
| F12 | note | dsyrk call: UPLO/TRANS/LDA/LDC correct | No action. | — |
| F13 | minor | comoment3 expert's per-thread `bp` panels are deallocated inside the parallel region, i.e. before the stop line, contradicting the "all deallocation after the stop" claim | RECORD (eight small frees, negligible) rather than restructure; fix the blanket claim in fortran-notes and README ("retained program arrays are deallocated after the stop; worker panels and compiler temporaries have their own lifetimes"); keep the machine-code store audit as the guarantee, not "structural" source arguments. | FORT, ARCH |
| F14 | minor | stale documentation: serial is not "directive-free" (standalone simd is active); onepass "no licence" comments; gram spec lines 13-14 contradict 105-118; comoment3 848,146; bench_support "no .mod" claim; README still says bound mt8; config.json's compute control is licensed where README says repro | ACCEPT all: README "serial = no active thread-level OpenMP"; fix the gram spec contradiction; onepass comments; bench_support comment; README binding text (already revised to unbound — verify); BOTH controls run serial x REPRO (README is authoritative; config.json follows). | ARCH, FORT, HARN |

Re-verification after the patches (FORT): both compilers, both tiers, serial and mt8, licensed
and repro, twins; natural vs expert cell-by-cell from the `-DBENCH_DUMP` builds; pins (input
exact at 15 digits, checksum within 1e-12); the store audit re-done for every patched arm. The
runner re-smokes all regimes after the Blade rebuild anyway.
