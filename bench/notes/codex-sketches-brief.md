# Brief for the Codex sketching agent: microkernel sketches (planning only)

## Rules

- You may create and edit exactly ONE file: `bench/notes/microkernel-sketches-codex.md` (this
  directory). Nothing else is written.
- Do NOT compile, build, run, benchmark or execute anything: no `Blade.exe`, `g++`, `clang`,
  `gfortran`, `ifx`, `dotnet`, `python`, `make`, no scripts. Do not use `git`. Reading is free.
- Repository worktree root: `C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/bench-fortran`.
  All paths below are relative to it. `CLAUDE.md` at the root explains the language and the
  compiler.
- There is no length budget. Write as many sketches as you judge worthwhile.

## Task

Write microkernel sketches for the emission forms the Blade compiler SHOULD produce where it
currently loses to hand-tuned Fortran, plus the runtime changes the measured losses call for.
Each sketch, numbered S1, S2, ...:

1. **Target**: the kernel(s) and the emission site (`src/<file>.fs:<line>` or the runtime
   header) that produces the current form.
2. **Current form**: quote it from the archived generated code (paths below).
3. **Proposed form**: C++17 in the style of Blade's generated code and runtime headers; LLVM
   IR shape notes where the LLVM lane differs.
4. **Legality**: whether every output cell keeps its summation order (strict-legal, valid under
   `BLADE_FP_REPRO=1`) or whether the sketch needs the reassociation licence.
5. **Budget on Zen 3**: 16 ymm registers, 2 loads/cycle, 2 FMA units with 4-cycle latency
   (8 independent accumulators to saturate), 32 KB L1d, 512 KB L2, 16 MB L3; store-forwarding and
   permute-storm pitfalls (`vpermpd`/`vunpck` storms have been seen in this repo).
6. **Payoff**: computed from the measured rates below (loads per FMA, lanes per cycle, page
   faults removed), as a forecast clearly labelled as such.
7. **Edges**: triangle boundaries, the rank-3 simplex, ragged tails, the empty last row of a
   strict triangle.
8. **Verification**: raw-bit tests against a serial reference; `vfmadd` counts under
   `-ffp-contract=fast`; what only a build can answer.

Finish with a ranked table (payoff per effort) and a list of open questions that only a build
could answer.

## Measured context

Benchmark suite in `bench/` (protocol `bench/README.md`, results `bench/RESULTS.md`):
Blade vs gfortran 16.2 and Intel ifx 2026.1 on an AMD Ryzen 7 5800HS. Blade beats the natural
(dense baseline) Fortran tier by 4–17x on the symmetric kernels but loses to the reviewed
expert tier. Serial, licensed regime (Blade's default: every source-licensed fold reassociated),
medians in ms, `blade-llvm / blade-cpp / gfortran-expert / ifx-expert`:

| kernel | blade-llvm | blade-cpp | gf-expert | ifx-expert |
|---|---|---|---|---|
| antisym (L = XYᵀ − YXᵀ, p=1201, n=401, strict packed) | 74 | 108 | 31 | 30 |
| comoment3 (T(i,j,k) = (1/n) Σ_s X(i,s)X(j,s)X(k,s), p=171, n=201, SymIdx<3>) | 41.5 | 110 | 11.5 | 11.7 |
| gram (G = XXᵀ, p=1601, n=1001, packed symmetric) | 157 | 275 | 82 | 73 |
| pairkernel (Matérn-5/2 matrix, n=2503, d=3, scalar exp per cell) | 42.6 | 77 | 33 | 31 |
| recur (RK4 of u − u³, nt=301 × nx=20011, `let rec`) | 117 | 134 | 10.5 | 11.3 |
| stencil (5-point Laplacian 1501×2003 + Σ²) | 13.5 | 16.3 | 14.5 | 11.6 |

Under `BLADE_FP_REPRO=1` (every fold one ascending chain) Blade's gram is 1219 ms while the
experts are unchanged: their register-tiled outer-product order keeps each cell's summation
order and still vectorizes over i. Threaded (8 threads, licensed): experts still 2–5x ahead on
the symmetric kernels.

Diagnosed mechanisms (file:line evidence in `bench/notes/gap-register.md`, G1–G27):

- **G1** gram/antisym: Blade's per-cell dot reloads both operand rows (2 loads per FMA, 16 B per
  lane) vs the expert 8×6 register tile (0.67 loads per FMA). Rates: gram LLVM 8.4 G FMA/s,
  C++ 4.4, experts 16–18. The C++ lane ALREADY recognises gram's nest as a syrk
  (`src/LinAlgPatterns.fs:1461` `(|BlasL3|_|)`, dispatched `src/CodeGenLoopNest.fs:2546`) and
  falls back to the plain nest only because the BLAS gate is off; a bitwise 6×8 packed GEMM
  tile ships in `src/cpp/blade_packed_gemm.hpp`. The LLVM lane picks 64×64 cache bricks with a
  per-cell dot (`src/EmitLlvm.fs:4047-4061`, `:2015`, `:2613`).
- **G1b** comoment3: multiply + FMA with 3 loads per term (a·b recomputed for every k) vs the
  expert's hoisted W = X(i)X(j) panel and one FMA at 0.67 loads; vector-op ratio 1.97x.
- **G26** C++ lane: `prodsum`'s licensed fold is ONE `omp simd reduction` accumulator, not
  unrolled; the loop-carried `vaddpd` (3-cycle) caps it at 1.33 lanes/cycle (`src/CodeGenExpr.fs`
  ~394–440).
- **G3a/G3b** checksum tax: the logical-cell checksum pass is 36 % (comoment3) / 21 %
  (pairkernel) of LLVM time and 70 % / 57 % of C++ time, because each canonicalizing read calls
  `canon_fold<R>` with an out-of-line `std::sort` (`src/cpp/index_types.h`), then
  `canon_left_justify`, then a nested-pointer load; the C++ lane reads the cell TWICE for `t*t`.
  pairkernel's whole 1.36x loss is this pass. No canonical-cell fold exists (**G2/G3**).
- **G6** recur: 8 temporaries of 160 KB allocated and freed per time step; the Windows heap
  returns them as fresh pages (~38 page faults each) = ~91k faults = the whole 118 ms; plus a
  48 MB zero-fill of the trajectory (`src/TypeCheckInfer.fs:13840`) and a 48 MB per-step copy.
  Arithmetic ~2.5 ns/cell-step, fully vectorized. One block kernel runs at expert speed.
- **G9** maskagg: `compound` builds an `unordered_map` node per kept cell (77.8 ns each).
- **G25** threaded folds are serial within each Path B chunk (no simd lanes in the chunk).
- **G14/G27** `prodsum` has no threading slot; `omp` on a row map is silently dropped.

## Read first, in this order

1. `bench/notes/phase2-microkernel-plan.md` — a 12-item plan by an Opus agent with a previous
   Codex session attacking it. Do not restate it: go beyond it, disagree where you can justify
   it, cover what it left thin.
2. `bench/notes/gap-register.md`.
3. `bench/notes/llvm-acceptance-A.md` sections 8, 11, 13, 14; `bench/notes/llvm-acceptance-B.md`
   sections 5–6; `bench/notes/fortran-notes.md` sections 8b–8c.
4. The expert Fortran microkernels to beat: `bench/kernels/gram/expert.f90` (`tile`),
   `bench/kernels/comoment3/expert.f90`, `bench/kernels/antisym/expert.f90`,
   `bench/kernels/recur/expert.f90`.
5. The Blade sources `bench/kernels/<k>/<k>.blade`.
6. Archived generated code: `bench/results/2026-10-18-LAPTOP-350H6LDB/session-042223/build/<kernel>/`
   (serial; `generated/*.cpp`, `*.ll`, `plan.txt` per arm) and
   `bench/results/2026-10-18-LAPTOP-350H6LDB/session-113651/build/` (threaded).
7. Compiler emission sites: `src/CodeGenLoopNest.fs` (comm triangle nest; row-fold jam near
   line 2401; simd-reduction emission), `src/CodeGenExpr.fs`, `src/CodeGenExprSupport.fs` (fold
   licence, K-lanes), `src/CodeGenBinding.fs` (~3300, ~3600), `src/EmitLlvm.fs`
   (`emitCompactFold` family, bricks, FMF near 1004), `src/LinAlgPatterns.fs` (~1461),
   `src/cpp/blade_packed_gemm.hpp`, `src/cpp/index_types.h`, `src/cpp/nested_array_utilities.hpp`,
   `src/cpp/blade_llvm_shim.c`, `src/Optimize.fs`, `docs/plans/plan-simplex-blocked-compute.md`.

## Topics to cover at minimum (add your own)

1. The packed-triangle syrk tile for same-array row contractions under `comm` (gram),
   reusing `blade_packed_gemm.hpp` at the BLAS-gate-off branch: triangle edges, KC blocking,
   both lanes.
2. The paired strict tile for antisym (two dots fused, the subtraction, no stored diagonal).
3. comoment3's rank-3 simplex tile with the hoisted W = X(i)X(j) panel and divide-then-store.
4. A K-lane / multi-accumulator form for the C++ lane's `prodsum` simd reduction (4 vs 8
   accumulators and why).
5. The inlined canonicalizing read for rank 2 and 3 (compare-swap network, flat pool offset,
   bind-once for `t*t`).
6. The orbit-weighted canonical-cell fold that removes the logical checksum pass (weights 1/2
   at rank 2, 1/3/6 at rank 3) and the Blade surface spelling it could take.
7. The runtime allocator: an exact-size retained-block cache / size-class free list in both
   allocators; the alternatives (hoist `let rec` step temporaries; fuse the RK4 step chain);
   destination passing for the two 48 MB passes.
8. maskagg: prefix-count / scatter compaction replacing the hashed compound index for rank-1
   masks.
9. Threaded folds that are simd within each Path B chunk (G25).
10. Fusing the checksum fold into the producing nest so the logical pass disappears under
    threads as well.
11. The LLVM lane: emit the tile as IR, or as a shared C core compiled once? Argue both.
12. Anything else in the archived generated code that looks wasteful and that you can name
    with a line reference.

## Output

The single file `bench/notes/microkernel-sketches-codex.md`. Reply with a short summary: the
sketch list, one line each, and the ranked table.
