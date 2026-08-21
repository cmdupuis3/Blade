# Direct LLVM backend — tiered optimization inventory and design

**Status (2026-08-18): IMPLEMENTED through M5 and MEASURED — see §0.** The backend
exists, ships behind `BLADE_LLVM`, and has been benchmarked against the g++ lane. §0
is the record of what landed and what the numbers said; everything from §1 down is the
2026-08-17 design audit that preceded it, kept verbatim as the prediction the
implementation was scored against. Where §0 and a later section disagree, §0 is the
measurement and the later section is the forecast.

---

## 0. Status 2026-08-18 — implemented through M5, and measured

Five commits on `feat/llvm-backend`: the scalar vertical slice, dense arrays and loop
nests, the `blade test llvm` harness, the fact layer, and the packed-simplex/blocked
nest. A sixth adds `blade test llvm-bench` and this section.

### 0.1 What emits

`src/EmitLlvm.fs` (module `Blade.EmitLlvm`, `tryEmitProgramNamed : string -> IRProgram
-> Result<string, string>`) emits textual `.ll` for a whole program or refuses the whole
program by name — never a half-module. `clang file.ll -O3` produces the executable in
one invocation (probe P2 of §1, confirmed in production); `src/cpp/blade_llvm_shim.c`
supplies alloc/panic/clock/print with byte-identical output formatting to the C++ lane.

Covered: the scalar core; dense static-extent arrays, loop nests, folds and array
printing; the fact layer (function/parameter attributes and licensed FMF, with
`BLADE_LLVM_FACTS` killing all three classes or any one of them); packed
`SymIdx`/`AntisymIdx` pools **at any rank** — triangular map nests, mirror reads,
antisym sign, empty diagonal — plus the blocked-simplex decomposition behind
`BLADE_LLVM_BRICKS` (rank 2 only; rank 3+ runs the serial simplex).

*Rank ≥ 3 landed 2026-08-19* and is worth its own line, because it was the largest
remaining refusal after `IRForRange`: `SimplexBlocksCore.prefixTerm` supplies the
per-level offset in closed form, `emitSimplexSerialR` hoists one term per level down
an r-deep nest, and `canonRead` canonicalizes by sorting network with the
antisymmetric sign as the exchange parity. Measured at **92% of r! at rank 3 and 95%
at rank 4**, with per-cell addressing cost FLAT across rank — where the emitted C++
lane's rises, so the llvm lane overtakes it at rank 4 *emitted-vs-emitted*. The
home-turf control (2026-08-20) then amended that reading: hand-erased C++ with
flat-pool addressing wins at BOTH ranks (and clang-vs-g++ is neutral), so the rank-4
gap is an ADDRESSING artifact the C++ emitter can recover — the backend-independence
theorem (§1-2) holds — and the same control found the llvm lane's own emitted IR
13-20% behind clang on clean C++ of the identical algorithm, an IR-shape headroom
item for this lane. Full tables in `plan-simplex-blocked-compute.md` §0a-0b.

Refused, by name, at whole-program granularity: runtime extents; providers; CUDA, MPI
and OpenMP emission; ragged/sparse/compound/orbit index types; Hermitian and wreath;
the BLOCKED schedule at rank ≥ 3; `IrrepsIdx` literals; element writes into compact
storage; outer products over compact storage; complex arrays. Compact symmetric
*folds* are refused earlier still, by the front end (BL3999, five sites in
`src/TypeCheckInfer.fs`) — that refusal binds both lanes.

**The largest single gap is `IRForRange`**, which has no arm. It is what `let rec`
lowers through, so the entire `recursive-arrays` corpus refuses (8 of 9 files; the
ninth is complex-valued), and it is also what a whole-rank fold (`reduce(A, (+), axes =
k)`) lowers through. One arm buys phase P3 and the missing fold spelling together.

### 0.2 Codegen speed — the R6 claim, confirmed at 4.5x

`blade test llvm-bench`, six programs, five alternating reps each, median; lowering is
outside the stopwatch (shared work); the C++ lane's content-addressed exe cache is
pinned off, since the llvm lane has no counterpart and leaving it on would time a file
copy against a compiler. Ryzen 5800HS, g++ 15.2.0 (ucrt64) vs clang 22.1.8 (clang64),
both at `-O3 -march=native`.

| program | kind | g++ ms | clang ms | ratio | .cpp bytes | .ll bytes |
|---|---|---|---|---|---|---|
| bench_scalar_chain | scalar | 852 | 177 | **0.21x** | 5 618 | 6 691 |
| bench_scalar_calls | scalar + calls | 919 | 183 | **0.20x** | 5 601 | 6 147 |
| bench_dense_pipeline | dense, large kernel | 1 070 | 322 | **0.30x** | 24 341 | 28 664 |
| bench_dense_elementwise | dense 1e7 map | 859 | 193 | **0.22x** | 6 173 | 3 020 |
| bench_dense_fold | dense 1e7 fold | 856 | 187 | **0.22x** | 5 580 | 2 842 |
| bench_sym_map | symmetric n=2003 | 935 | 247 | **0.26x** | 7 332 | 9 314 |

**Median ratio 0.22x — the llvm lane reaches an executable 4.5x faster**, and the effect
is far outside the run-to-run spread (g++ 846–1172 ms across all reps, clang 176–352 ms;
the two ranges do not touch). Two honest deductions from the headline: a build that must
also compile `blade_llvm_shim.c` costs 292 ms rather than 177 ms (once per output
directory, reused by every later link), and a *warm* C++ rebuild of unchanged source
costs 14 ms because the exe cache short-circuits g++ entirely — a cache the llvm lane
should acquire before anyone quotes 4.5x as a user-facing latency figure.

The mechanism is the obvious one and is exactly R6 (§3): the C++ lane hands g++ a
translation unit that `#include`s the runtime headers and pays a C++ front end for them,
while the llvm lane hands clang a file with no front end left to run. Emitted size does
not explain it — the `.ll` is *larger* than the `.cpp` on four of six programs.

*Reproducibility: three independent whole-block runs gave per-program ratios of
0.21/0.20/0.30/0.22/0.22/0.26, 0.21/0.20/0.30/0.22/0.22/0.26 and
0.21/0.21/0.31/0.22/0.22/0.26. The table is run 2.*

### 0.3 Runtime — parity, exactly as predicted

Four shapes, all at non-power-of-two extents; arms rotated round-robin, one warmup
discarded per arm per round, 9 reps × 3 rounds = 27 samples per arm, medians. "inner" is
the program's own `completed in` clock around the compute region; "outer" is whole-process
wall time and carries ~25 ms of Windows process startup, which compresses every ratio
toward 1.0. Values are compared across arms before any timing is reported.

| shape | arm | inner med | inner min–max | vs cpp | outer med |
|---|---|---|---|---|---|
| dense elementwise, n = 9 999 991 | cpp | 34.518 ms | 34.0–37.2 | 1.00x | 59.3 ms |
| | llvm | 33.873 ms | 31.7–36.0 | **0.98x** | 61.7 ms |
| dense licensed fold, n = 9 999 991 | cpp | 24.777 ms | 24.1–25.6 | 1.00x | 49.4 ms |
| | cpp-reassoc | 20.356 ms | 20.0–29.1 | 0.82x | 44.7 ms |
| | llvm | 22.623 ms | 22.0–38.1 | **0.91x** | 47.5 ms |
| | llvm-reassoc | 18.819 ms | 17.6–20.0 | **0.76x** | 43.2 ms |
| symmetric map, n = 2003 | cpp | 3.010 ms | 2.8–3.8 | 1.00x | 28.2 ms |
| (C(2004,2) = 2 007 006 cells) | llvm-serial | 4.255 ms | 3.5–6.3 | 1.41x | 28.3 ms |
| | llvm-bricked | 4.239 ms | 3.8–5.8 | 1.41x | 27.9 ms |
| symmetric map, n = 6007 | cpp | 31.763 ms | 29.7–34.3 | 1.00x | 59.0 ms |
| (C(6008,2) = 18 048 028 cells) | llvm-serial | 31.303 ms | 29.1–37.2 | **0.99x** | 61.5 ms |
| | llvm-bricked | 34.125 ms | 31.0–36.9 | 1.07x | 64.2 ms |

**The llvm lane runs at parity.** On the two large shapes where the measurement is solid
it is 0.98x and 0.99x — inside the noise. The one apparent loss, 1.41x on the symmetric
map at n = 2003, is 1.2 ms of absolute difference on a 3 ms measurement whose own
min–max spans 2.8–6.3 ms; the same program at 9x the size shows 0.99x, which is the
reading to trust.

The fold row is the interesting one, and it does not favour a backend. `BLADE_FP_REASSOC`
buys 18% on the C++ lane (24.777 → 20.356) and 17% on the llvm lane (22.623 → 18.819),
while switching backend at fixed license buys 9% (24.777 → 22.623) — so **the
reassociation license is worth about twice what the choice of backend is worth on the one
shape where the backend difference is even measurable, and infinitely more on the shapes
where it is 0.98x** — and both lanes can spend that license. That is the fact-emission
thesis being tested and coming back the way §1's audit predicted: the win lives in *what
code the front end decides to generate*, not in which assertion channel carries it.

### 0.4 Bricks — the P1 gate of the simplex plan does not pass

`plan-simplex-blocked-compute.md` §9 P1 sets the gate as "parity-or-better with ≥1 clear
win". Measured, at matched n, llvm-bricked against llvm-serial: **1.07x slower at
n = 6007** and indistinguishable at n = 2003 (1.41x vs 1.41x, both inside that shape's
noise). No shape showed a win. The decomposition is correct — the three-way gate proves
bricked, serial and C++ agree cell for cell at two tile edges — and it costs about 7%.
A third run reproduced it: 1.06x at n = 6007, with llvm-serial at 1.01x.

This is precisely what that plan's risk 1 warned about ("measure-first; the 2.14x packed
precedent"), and it is a *negative result on the S0 variant only*: what was measured is
brick iteration over the existing packed pool, which is the cheapest variant and the one
that changes nothing about layout. S1 (brick-major layout, contiguous alignable tiles)
and the mirror-expansion contraction modes are untouched and unmeasured, and they are
where the plan's actual claim lives. The honest status is *S0 measured, no win; keep the
knob for measurement, do not take the decomposition by default*.

**Resolved 2026-08-18 (same branch): the default is the serial triangle.**
`SimplexBlocksCore.autoTileEdge` now returns `None` at every extent, so with
`BLADE_LLVM_BRICKS` unset the emitter never blocks — the default emission is the fastest
measured path, as the design invariant demands. Bricked emission is opt-in via an
explicit `BLADE_LLVM_BRICKS=<B>` (the bench's bricked arm pins B = 64), kept precisely so
S1 / mirror-expansion variants can be A/B'd against a proven-correct control. The real
crossover n, if any, stays unmeasured. Tracked in plan-simplex-blocked-compute.md §0.

### 0.5 The decision gate that was skipped, and what the numbers say about it

§4 specifies P0 — a clang-lane A/B on the *C++* path, costing about a day, gating all
backend work — with the decision rule "if clang+macros lands within the documented ~15%
of a hand-written `.ll`, the backend cannot be justified on fact emission". P0 was never
run; the backend was built first.

The runtime numbers above retroactively return P0's verdict anyway, and it is the one §4
predicted: at parity on every solid shape, with the licensed-reassociation knob worth
about twice the backend choice where the backend is measurable at all, **fact emission
does not justify this backend.** What does
justify it is R6, the argument §3 called "the strongest honest case" — toolchain
consolidation and compile time — now measured at 4.5x and reproducible across two
independent runs. The lane earns its keep as a *fast* path to an executable and as a
second host compiler for miscompile cross-checking, not as a faster one.

### 0.6 Harness

```
blade test llvm          # 6 blocks: compare rules, emission pins, fact layer,
                         # simplex agreement, blocked-simplex three-way gate, differential
blade test llvm goldens  # the 4 toolchain-free blocks; instant, no g++ needed
blade test llvm blocks   # simplex agreement + three-way gate (aliases: simplex, bricks)
blade test llvm <dir>    # the differential over one literal tests/corpus/<dir>
blade test llvm-bench    # this section's two tables (alias: blade test llvm bench)
```

All standalone: none is reachable from `blade test`, by design — the differential spawns
two native compilers per corpus file and the bench spawns hundreds of processes. Neither
bench half can fail on a slow ratio; both fail on a refusal, a build error or a value
disagreement, because at these fixtures those are coverage regressions.

---

**Status of the original audit: EXPLORATORY — and the headline thesis did not survive its own audit intact.**
Written 2026-08-17 from a five-agent research pass: an external evidence dossier (LLVM
22.1.8 stable / 23.1 rc as of this writing; fact-channel catalog; Rust/Fortran/Futhark/
Halide/ISPC/Polly/XLA prior art; scan-compilation state of the art), two deep analyses
(a repo-wide semantic-capital census and a backend architecture design), a nine-language
comparative taxonomy, and an adversarial red team that ran fresh structural probes on this
machine (Ryzen 5800HS, clang 22.1.8 at `C:\msys64\clang64\bin`). Companion to
`docs/plans/plan-mlir-backend.md` — deliberately separate documents; §9 splits ownership so the
two never bill the same win twice.

The naive thesis was: *Blade's front end proves facts (aliasing, extents, algebraic
licenses, recurrence structure, layout freedom, closed world) that C erases; an LLVM
backend can assert them directly instead of hoping g++ rediscovers them.* The audit
narrowed it in three ways, and this document is organized around the honest version:

1. **Most provable facts are exploited by choosing what code to generate, which is
   backend-independent.** Scan routing, stride padding, fusion, literal trip counts,
   triangular packing, closed-form induction — in every one, the front end consumes the
   fact by emitting a different loop nest, and that nest is expressible as C++ text as
   easily as LLVM IR. The repo has already banked five such wins through the C++ text
   channel with no backend change (§1).
2. **Most of the remaining "assertion" channels have C-level spellings clang honors.**
   Fresh probes: `#pragma clang fp reassociate(on) contract(fast)` at block start flips a
   plain f64 sum from 0 to 10 `vaddpd` (per-block FMF from C++); both gcc and clang
   already vectorize alias-ambiguous loops via runtime versioning, so `noalias` buys
   removal of a check-branch and a duplicated body, not vectorization itself. The repo
   already routes vendor spellings through one header (`src/cpp/blade_portability.hpp`);
   a clang lane is one `#elif` per macro.
3. **What is genuinely LLVM-only is small and mostly unmeasured** (§3): custom calling
   conventions, `captures(...)`, `"separate_storage"` assume bundles, direct `llvm.vp.*`
   — plus the two arguments that are not fact-emission arguments at all: compile time /
   toolchain consolidation, and a future ORC-JIT lane for data-dependent shapes, which is
   where the real moat is (§7, explicitly a different document).

A prior decision is on record and must be confronted: the deleted perf-exploitation plan
concluded "LLVM/asm rejected — clang C++ IS LLVM; only JIT runtime-shape specialization
is unreachable from AOT C++" (recoverable via `git show
a2d8b4d:docs/plan-cpp-perf-exploitation.md`; the file was deleted at ef94c50 but is still
cited by docs/plans/plan-static-array-erasure.md:79,318,386 and five source comments —
IRMono.fs:1623, CodeGenExpr.fs:2377, CodeGenExprSupport.fs:1070, Diagnostics.fs:266,
ParserPatterns.fs:183). This
document does not overturn that verdict; it narrows the LLVM case to the residual set and
gates everything behind a one-day experiment (§4) that requires zero backend code.

---

## 1. Evidence base

**Banked already, through the C++ text channel** (no backend change involved — these
discipline every "LLVM would unlock X" claim):

| win | mechanism | measured |
|---|---|---|
| flat-pool elementwise mode | frontend picks flat traversal; g++ fully unrolls the 12-trip inner loop | 2.0x at 1000x12 |
| per-site FP reassociation | `omp simd reduction` / K-lane forms, license-gated (`foldReorderLicensed` [CodeGenExprSupport.fs:1123](../../src/CodeGenExprSupport.fs), `fpReassocEnabled` [CodeGenState.fs:498](../../src/CodeGenState.fs)) | 1.64x dot, 2.60x gemv fiber, 3.36x 3-stream former |
| K=8 lane accumulators | fixed-lane ILP emission | 1.99x at n=1e7 |
| shadow-frame elision | frontend panic-reachability analysis | 15.0x fold / 9.4x map |
| shape monomorphization | literal inner bounds in fiber kernels | the 1.77x axis below |

**Measured zeros** (each killed a seed claim's naive form): `restrict` on read operands =
1–3% noise in every regime (docs/plans/plan-static-array-erasure.md:207,273 — the *output* row
was already restrict-qualified and read/read aliasing was never a barrier); the
`Array<T,N>` ABI = 0% (SROA erases the wrapper; byte-identical asm to a hand-erased twin);
packed triangular layout = **2.14x slower** than dense when applied naively; the "~7x
power-of-two artifact" is a benchmark-hygiene rule about associativity conflicts, not a
recoverable prize (intrinsic stride penalties at n=1025 are 1.12–3.36x).

**Fresh structural probes (2026-08-17, this machine):** (P1) `C:\msys64\clang64\bin` has
clang 22.1.8 + `opt`/`llc`/`llvm-as`/`lld` installed and already drives a Blade lane
(`BLADE_MEMCHECK`, Build.fs:424-448); no `mlir-*` tool exists — resolving the MLIR plan's
Windows-toolchain VERIFY asymmetrically in LLVM's favor. (P2) `clang t.ll -o t.exe`
compiles textual IR in one step — no `opt`/`llc` orchestration needed. (P3) gcc 15.2 and
clang 22.1.8 both vectorize an alias-ambiguous peel-shaped loop at -O3, gcc noting "loop
versioned for vectorization because of possible aliasing" — aliasing ambiguity costs a
versioning scaffold, not the vectorization. (P4) `#pragma clang fp reassociate(on)
contract(fast)` (must open a braced block; Blade's reassoc sites already do) vectorizes
the plain sum.

---

## 2. The optimization inventory, in tiers

Tier definitions: **TIER 1** — exploits information other languages' semantics erase or
cannot express, even with annotations. **TIER 2** — achievable elsewhere in principle but
rarely achieved, or achieved only via unchecked programmer promises where Blade has a
typechecked license. **TIER 3** — general-purpose backend hygiene.

Each entry carries the two-axis honesty the audit demanded: the *tier* rates the language
capability; **delivered** / **unbuilt** and the *consumption point* (F# emission /
C++-spellable / LLVM-only) rate what a backend actually gains. Comparative verdicts are
from a nine-system survey (C/C++, Fortran, Rust, Julia, Halide, Futhark, ISPC, XLA/JAX,
Polly).

### TIER 1 — other languages can't do this

**T1.1 Symmetric/antisymmetric/Hermitian derivation + packed triangular storage.** The
strongest, least contestable item. `where comm(...)` + identity-group deduction turns a
symmetry *declaration* into an iteration domain and an output storage class
(`SymcomState` Types.fs:89, `computeAllSymcomStates` IRLoopStructure.fs:302,
`computeTriangularBound` IRLoopStructure.fs:461); antisym stores no diagonal, with
`StrictOffset` (IRStorage.fs:63) confined to flat reads and loop-bound shrink
(CodeGenLoopNest.fs:155); symmetric output storage is licensed only under proven
same-array occupancy (`IRGram(_,_,isSameArray)` IR.fs:150 — a C compiler can never
legally assume A≡B), and the refusal is proved correct. Iteration *and* storage shrink by
r!. No surveyed system derives triangular iteration from a type — Polly can optimize a
hand-written triangular loop but cannot invent one, and the packed offset (a sum of
binomial coefficients) is non-affine: outside polyhedral reach, outside `linalg`, outside
CuTe's multilinear layout algebra (plan-mlir-backend.md:151-155). Family members: sign
parity proofs (`KernelSignParity` proven from the body, carried on
`IRCallable.SignParities` IR.fs:316, consumed by the wreath-tie soundness gate
IRLoopStructure.fs:559-658) and
the omp-vs-BLAS routing table's syrk rationale (LinAlgPatterns.fs:478-531).
**Delivered — and backend-NEGATIVE for LLVM**: the C++ path already emits this optimally
(the comoment3 nest is byte-identical to a hand-erased twin), non-affine subscripts are
exactly where LLVM's dependence analysis is weakest, and the shipped mitigation
(flat-pool traversal of compact storage, zero coordinate math) is backend-agnostic. The
LLVM lane's only job here is *carrying* it, which the MLIR path can't do until a `blade`
dialect exists — a real but modest coexistence point (§9). A decomposition route that
would revise this verdict — covering the simplex with dense affine bricks and confining
the non-affine part to a 2^{-d} residue — is specced in
`docs/plans/plan-simplex-blocked-compute.md`.

**T1.2 Reynolds-manufactured commutativity.** `reynolds(g)` *synthesizes* a commutative
kernel by symmetrization (`IRReynolds` IR.fs:95; n! term count carried statically :368)
— the compiler creates an algebraic license where none existed. Halide's `rfactor`
associativity prover is the strongest competitor and is strictly weaker in kind: it
proves or rejects what you wrote; Reynolds changes the answer to yes by construction.
Nothing else in the survey does this. **Delivered** (feeds the same license machinery as
T2.1). Consumption: F# emission.

**T1.3 Recurrence structure without loop recovery — conditional, unbuilt.** `let rec`'s
recursion axis is syntactically the leading axis with a static extent; `guardsFor`
(TypeCheckInfer.fs:9577) classifies prefix reads into `n`, `n±c`, `const` with exact
compile-time lags. That hands over, for free, the half of the scan-synthesis problem
that defeats every C/Fortran auto-parallelizer (dependence recovery from pointer
arithmetic) — automatic recurrence→scan has *no production precedent*; the research
frontier is one month old (ScanWeaver, arXiv 2606.00601). But the audit's corrections
are decisive: (a) the recognizer covers exactly four index shapes — anything else falls
to guarded-unknown; (b) nothing proves the step linear or associative — the only
associativity machinery is a builtin-op recognizer (`foldKernelBuiltinOp`
CodeGenExprSupport.fs:1102), and building
linearity analysis *is* the open research problem; (c) **the corpus has no scannable
program at scale** — recursive-arrays extents top out at Idx<6>, and the two long
recurrences in examples/ (QG atmosphere at Idx<1601>, Burgers LES) are nonlinear steps
scan cannot touch; (d) FP scans reassociate, so scan output breaks both byte-identity
oracles and must be license-gated like `BLADE_FP_REASSOC` — the default build never
takes it. Futhark ships `scan` as an explicit primitive and that design is arguably
sufficient. **Verdict: tier 1 as a premise; file as the highest-ceiling research item;
write the missing benchmark program before any implementation (§4); and note the
eventual transform is a front-end deliverable emittable as C++ anyway.**

### TIER 2 — difficult in other languages, or checked where others guess

| # | optimization | Blade fact (proof site) | best competitor | status / consumption |
|---|---|---|---|---|
| T2.1 | Per-site FP reassociation licenses | `foldReorderLicensed` (CodeGenExprSupport.fs:1123) = declared `comm` ∨ groups ∨ recognized builtin body, cross-checked against body parity; BL4016 refuses unlicensed parallel folds | Halide *proves* associativity (stronger leaf); C/Fortran have TU-wide flags or unchecked pragmas | **Delivered** via `omp simd reduction`/K-lanes (1.64–3.36x); clang pragma closes the rest (probe P4). LLVM adds per-*instruction* granularity no Blade license demands — capability without demand |
| T2.2 | Deterministic licensed reassociation | fixed K-lane accumulation shared across all emitters so the answer can't drift between sites (`fpReassocLaneStmts` CodeGenExprSupport.fs:1194) | `-ffast-math` destroys run-to-run determinism; no surveyed system offers "canonically reassociated" | **Delivered.** A genuinely unusual property worth advertising: reassociated *and* reproducible |
| T2.3 | No-versioning-scaffold codegen (the correct framing of static extents) | shape monomorphization bakes literal trip counts; measured 1.77x on a short fiber, mechanism = the eliminated runtime-remainder scaffold (55→25 instructions) | gcc/clang version loops at runtime for alias/alignment/trip; Julia/XLA specialize per signature (equal); Futhark size types (equal) | **Delivered in principle; 83 of 472 surveyed loop headers still load `.extents[d]`** — an S-effort C++ emitter fix (route through `literalOrRuntimeExtent`), no backend content |
| T2.4 | Extent-agreement proofs | BL3016 at both seams (Unify.fs:135-146, `extentClash` TypeCheckInfer.fs:3393 / `kernelExtentClash` :7858) + halo twin + runtime BL8009 | nothing checks this statically in C-family; Futhark size types equal | **Delivered**; literal-only — runtime equalities are assumables (`llvm.assume icmp eq`), not provables |
| T2.5 | Guard-free indexing via index provenance | `Nat<LatIdx>` ≠ `Nat<LonIdx>` at equal extent; no bounds checks *exist* to eliminate; index-tag arithmetic forbidden by design | Rust: checks-then-hope-LLVM-deletes; Julia `@inbounds` = unchecked UB; Futhark/XLA reach the same place | **Delivered** backend-independently; provenance is erased by IRProgram, so there is nothing left to assert to a backend — `!range` on loop counters is redundant with literal bounds |
| T2.6 | Deferral-driven buffer assignment / in-place update | whole pipeline dataflow known before any buffer chosen (deferred until `IRCompute`); `mut` array params the only aliasing channel; escape analysis trivial (`collectFreeVars` exhaustive) | XLA buffer assignment + Halide storage folding ship *better* machinery today; Futhark uniqueness types = checked in-place licenses | **The largest measured headroom in the memory family** (LSWOSA serial 1.35x deficit vs classic C attributed to temp allocs). An F# transform + `llvm.lifetime` markers; not backend-gated. **Lead the memory story with this, not noalias** |
| T2.7 | Typechecked parallel folds | `omp(x: n)` depth caps; BL4016; `OmpRequested` never silently dropped; FoldChunkPlan's defined partial order (IRStorage.fs:79-100) vs OpenMP's unspecified combine | rayon is checked (equal-ish); OpenMP/Julia unchecked; Futhark auto but operator associativity unchecked | **Delivered** to `#pragma omp`. The "optimizer transforms *through* the license" upgrade is MLIR's (`scf.forall`), not LLVM's — LLVM has no parallel construct; `!llvm.loop.parallel_accesses` is a vectorizer fact, and hand-emitting `__kmpc_*` would trade a stable pragma surface for a version-pinned private ABI |
| T2.8 | Symmetry-derived schedule policy | triangular imbalance ⇒ `schedule(dynamic)` derived, not chosen (CodeGenLoopNest.fs:584, CodeGenFusion.fs:75) | OpenMP users hand-pick; Halide autoschedules (better ceiling); no annotation system has the structural fact | **Delivered.** A family the seed list missed entirely |
| T2.9 | Typed-algebraic BLAS/LAPACK routing | routing from `gram`/`prodsum` algebraic forms + identity groups, policy as one literal table with rationale (LinAlgPatterns.fs:478-531) | ICC pattern-matches loops (fragile); Julia dispatch, XLA routing (comparable-to-better coverage) | **Delivered and banked** (syrk symmetric wins measured). Stays out-of-band on any backend; `llvm.matrix.*` is not a BLAS mechanism |
| T2.10 | Layout ownership | no source-declared layout; padding/alignment/SoA legal where C's ABI forbids it | Halide storage directives + XLA layout assignment ship today; Rust reorders fields only | **Unbuilt; measurement-gated** (2.14x packed regression is the standing warning; pad-the-stride is 2^k-only per the 1.12–1.26x intrinsic figures). Allocator + nest builder are F#; zero backend content |
| T2.11 | Whole-language compile-time evaluation | StaticEval incl. static provider payload folding (StaticEval.fs:59-64); extent exprs fully folded | Julia `@generated`/Zig comptime at least equal; C++ constexpr weaker | **Delivered** (mechanism tier 3, coverage tier 2). Emit as constants + `unnamed_addr`; don't double-bill with T2.3 |
| T2.12 | Rank≥2 recurrences: serial-outer/parallel-inner by construction | whole-slice steps desugar to elementwise trailing-axis nests (TypeCheckInfer.fs:9469) | wavefront legality is a polyhedral research result elsewhere | **Delivered** shape; inner-loop parallelism annotation is C++-spellable |
| T2.13 | Group-by/ragged as CSR by construction | `IRGroupKeys`/`IRGroupSizes`/`IRRaggedLookup` (IR.fs:121-123,198) | hand-written CSR elsewhere; MLIR sparse_tensor comparable | **Delivered**; segmented-reduction lowering possible on any backend |
| T2.14 | Mask/compound branchless selection | selection materialized once, not per-iteration branching (IRMask IR.fs:99) | predication via `llvm.masked.*`/VP intrinsics is the LLVM spelling | **Delivered** semantically; gather-heavy code is bandwidth-bound — marginal |

### TIER 3 — general-purpose good practice (emit, don't advertise)

Complete alias map as *hygiene*: `memory(argmem: read)`/`memory(none)`, per-param
`noalias readonly writeonly align captures(none) noundef`, scoped `!alias.scope` domains
— the capability is real and total (`MutParamPositions` TypeEnv.fs:239 is the only write
channel; no pointers, no address-of, deep-copied mut locals), genuinely without Rust's
`UnsafeCell` carve-outs, but Fortran/Futhark/Halide/XLA have equivalent defaults and the
measured payoff here is 0–3% on every hot shape (the versioning scaffold P3 bounds it).
Emit because it's free and correct; do not bill it as a differentiator. Then: blanket
`nounwind willreturn mustprogress nosync nofree` (no exceptions, no unbounded loops —
recursive arrays terminate by construction; deletes all Windows SEH lowering; a family
the seed missed, free on day one); `norecurse` on the non-self-recursive majority;
`internal`/`private` linkage + `dso_local` + gc-sections; full LTO (not Thin — one
module + one shim TU); lifetime markers on per-block temporaries; 64-byte-aligned
allocation + `align` attrs; `cold noreturn` panic; light two-node TBAA; debug info from
`Span`s (panics already print `.blade` locations — DWARF replaces the 14x-costing
shadow-frame *runtime* mechanism with zero-cost metadata when frames are wanted);
PGO later (IR-level instrumentation is free once IR is valid, low priority); `IRForRange`
literal unrolling annotations; virtual arrays as bare induction variables (already true);
display/print emitters `cold`+`minsize`; vectorized libm via veclib routing with *no*
`afn` by default.

### Out of scope — no advantage, and the doc should own it

**Sparse codegen**: explicitly disclaimed by the language ("Blade is not a sparse-tensor
system"); hashed `SparseIdx` does not map to any sparse compiler's sorted-compressed
model. The one adjacency: statically-frozen key sets could compile to perfect-hash
tables (an F# + shim item, not a backend one). **Stencil time-tiling**: exact halo
offset sets are a tier-1-flavored *analysis input* (they are what Polly burns its SCoP
budget recovering), but Blade has no tiling machinery — Halide is far ahead and will
stay ahead. **Autotuning/schedule search**: structurally forgone by "the fastest way is
the only way" — Blade trades Halide's searchable ceiling for the guarantee that the slow
version is unwritable; own the tradeoff. **ABI freedom for speed**: measured 0% (SROA
already erases the wrapper). The honest ABI argument is *bug-class deletion* — flat
pools + extent scalars eliminate the dangling-extents-table class that bit all 14
`materialize*Form` builders — not cycles.

### Facts that do not exist (ruled out by the census; do not assume them)

No reified lag-set IR field (lags are recognized and immediately consumed pre-IR); no
scan/prefix primitive anywhere in the IR; no layout pass, no alignment discipline, no
aligned allocator; no cost model or autoscheduler; no PGO channel; no SIMD-width IR
annotations (all vectorization is delegated, except the fixed K-lane forms); no general
dependence analysis over kernels (the special forms — halo/shift/rec-array — are
exhaustive *because* index-tag arithmetic is forbidden); extent equality is not a
unification fact (only literal-literal seams are checked); no branch-probability facts;
and — a corrected premise — **the closed world is a source-level property the emitter
currently spends**: main-local function references emit as `std::function` locals
(CodeGen.fs:1241-1333, main-locality decided by `computeMainLocalFuncIds` :1429; corpus
witnesses functions/023, functions/086). Mutual recursion
is BL4006 ("mutual group violation", Diagnostics.fs:233); BL2001 is "unbound variable" —
the form mutual recursion *surfaces as* under define-before-use.

---

## 3. What is genuinely LLVM-only

R1. **Custom calling conventions** (`fastcc`, `preserve_most`) — real, inexpressible in
C++; expected value small since monomorphization + `-O3` + one-TU already inline the
hot graph. R2. **`captures(...)`** finer than `nocapture` (LLVM 20+) — helps escape
analysis on read-only views; unmeasured. R3. Per-instruction FMF — capability without
demand (no Blade license is finer than a block); *dropped*. R4. **`llvm.assume`
`"separate_storage"` bundles** — runtime two-pointer non-overlap, the one channel
mapping to something Blade knows and C cannot say (runtime-distinct operand arrays at
comm call sites). R5. Direct `llvm.vp.*` predication — speculative, bypasses the
vectorizer; *dropped*. R6. **Skipping the C++ front end entirely** — a measurable
compile-*time* win (the repo cares: compile-speed work took 10.4s→0.6s and C++ parse+
codegen is a real slice of what remains) and the eventual retirement of the MSYS2 g++
dependency, with clang+lld already installed and `.ll`-in-one-step confirmed (P1/P2).

**R6 and the toolchain argument are the strongest honest case for this backend. R1/R2/R4
are the honest fact-channel case. The JIT residue (§7) is the only moat.**

---

## 4. The decision gate: the clang-lane A/B (P0 — before any backend code)

Cost ~1 day; settles whether fact emission can justify the backend at all.

1. Add `BLADE_CXX` to Build.fs (unset → g++; `clang++` → clang64 root) — a function,
   never a module-level `let` (pin/restore discipline).
2. Wire two macros in `blade_portability.hpp`: `BLADE_IVDEP` → `#pragma clang loop
   vectorize(assume_safety)` (deliberately empty for clang today), and new
   `BLADE_REASSOC_BLOCK` → `#pragma clang fp reassociate(on) contract(fast)` (must open
   a braced block; the reassoc emitters already do).
3. Full suite on the clang lane; audit `, N skipped` before trusting green.
4. Benchmarks, non-power-of-two only, round-robin rotated arms, 9 reps after warmup, 3
   process rounds, medians, checksums pinned: dot n=10 000 019; comoment3 61x2003; flat
   elementwise 1000x12; gemv fiber. Arms: g++ today / clang+macros / clang+macros with
   restrict on outlined-kernel *parameters*, and a hand-written `.ll` reference.

**Decision rule:** if clang+macros lands within the documented ~15% round-to-round drift
of the hand-written `.ll` on these shapes — and probes P3/P4 predict it will — the
backend cannot be justified on fact emission, and proceeds (if at all) on §3's R6 +
R1/R2/R4 only. Either way the clang lane itself is likely worth keeping (second host
compiler = miscompile cross-check, and the memcheck lane already half-owns it).

The scan question gets its own gate: **write the missing program first** — a scalar
first-order affine recurrence at `Idx<10000019>`; arms: Blade-today serial / hand-C
serial / hand-C Blelloch at 1,4,8 threads. If scan doesn't beat serial by >1.5x at ≥4
threads, T1.3 dies regardless of backend; if it does, the next question is whether any
real Blade program has that shape (the corpus currently says no).

---

## 5. Architecture (conditional on §4)

**Bind point: textual `.ll` from a new `src/EmitLlvm.fs`, compiled+linked by clang in
one step.** Typed builders per instruction shape with attribute/metadata slots as named
fields (the EmitCpp.fs:1-10 discipline; `.ll`'s flat grammar is more builder-friendly
than C++); byte-pinnable golden files against *our* emission only; no P/Invoke surface
(LLVMSharp pins the process to one native build and couples harness lifetime to a native
lib). **SSA strategy: alloca + loads/stores everywhere; SROA/mem2reg rebuilds SSA** —
clang's own approach; deletes the dominance-frontier problem from the F# emitter.
Opaque pointers only. Version-pin one LLVM major; the doctor row compiles and runs a
real `.ll` through the resolved clang (never trust a PATH probe) and flags drift.

**ABI:** flat pool + baked extents. Static extents (the common case) → bare `ptr` with
literal constants in every GEP and bound; runtime extents → `ptr` + `i64` scalars as
separate SSA arguments, never a descriptor struct. Row views = `getelementptr inbounds`.
The Iliffe skeleton is simply not emitted; `DeviceBufferType`'s DFS pool order
(IRStorage.fs:175-191) is the shared ground truth, and the memref-descriptor form the
MLIR plan needs falls out of the same layer.

**Shim** (`blade_shim`, one TU compiled by clang, shared with the MLIR plan's Phase 0-1):
print/panic/alloc(64-aligned)/thread-pool-later. Print formatting is the underestimated
risk — every corpus EXPECT pin is a byte pin, so the shim's formatting holds to the same
lockstep discipline as the hand-rolled `lgamma`/`digamma` series (which take *no* FMF,
ever). One favorable inversion: g++ needs `-ffp-contract=fast` *suppressed* for byte
identity; LLVM IR contracts only where `contract` FMF is present — the default emission
is byte-identity-shaped and `BLADE_FP_CONTRACT` becomes a per-instruction opt-in.

**Fact layer:** a backend-neutral `src/BackendFacts.fs` computes typed fact records
(alias channel, purity, licenses, extents, lag sets, chunk plans) once; EmitLlvm
serializes them as attributes/FMF/metadata; a future EmitMlir serializes the same
records as discardable attributes. The `FoldWrapper`/`ShareDecl` typed-primary refactor
(plan-mlir-backend.md:103-105) is a shared prerequisite — do it once, before either
backend. Facts attach at instruction birth (builder fields), never by a later pass.
Prefer attributes/metadata over `llvm.assume` (assume-cascades pessimize); reserve
assumes for runtime-extent facts and R4.

**Pass pipeline:** clang's stock -O3 via the driver — every fact above is designed to be
consumed by stock passes; `BLADE_MARCH` maps to `-march=` verbatim. No custom passes
initially; the one justified plugin later is a *verification* pass (query AA for pairs
we asserted disjoint; run only under `blade test llvm`). Recurrence-to-scan, if ever, is
a front-end transform — licenses live in the front end, and no LLVM pass will recover
user-kernel associativity.

**Refusal and harness:** whole-program granularity first (`llvmBodyRefusal` names the
construct, falls back to C++) — the fact layer's biggest wins are whole-module
properties, and a per-kernel DLL boundary truncates exactly what the backend exists to
assert. Per-kernel hybrid later over the proven nvcc-DLL shape (`buildCublasDevice`
Build.fs:319).
`BLADE_LLVM` gate (function), `HasClang` capability probe (skip-not-fail),
`blade test llvm` standalone block, golden emission pins + EXPECT execution + llvm-vs-
interp diff on dense categories. Per-fact-class kill switches
(`BLADE_LLVM_FACTS=noalias:off,...`) so any miscompile bisects to a fact class in one
run.

---

## 6. Numeric policy (the licensed path is not the tested path)

Adopted from the existing contract (CodeGenState.fs:425-501) unchanged: (i) `reassoc nsz`
FMF only where `foldReorderLicensed` ∧ `fpReassocEnabled()`; (ii) `test interp` /
`diff-oracle` run with reassoc and contraction off, byte-for-byte, no float tolerance
ever enters InterpDiff — that gate has caught every real divergence; (iii) the licensed
path is tested by a separate block whose corpus uses integer-valued f64 (exact under any
association — the standing rule for parallel-reduce tests) plus invariant-based checks
(orthogonality residuals, reconstruction error) as the math corpus already does. Scan
emission, if built, sits behind the same license and the same split.

---

## 7. Phasing

The `outcome` column is the 2026-08-18 record; see §0 for the numbers behind it.

| phase | deliverable | gate | outcome |
|---|---|---|---|
| **P0** | clang-lane A/B (§4) — no backend code | decision rule §4; also settles Windows toolchain reality (already resolved favorably by P1/P2 probes) | **SKIPPED.** The backend was built first. §0.5: the P1/P2 runtime numbers return P0's verdict anyway, and it is the one §4 predicted |
| **P1** | scalar-program `.ll` emission end-to-end: EmitLlvm skeleton, shim, `BLADE_LLVM` gate, doctor row, golden pins, `blade test llvm` | scalar + dense corpus subset byte-identical to interp; `, N skipped` audited | **LANDED** (39d14ba, ec8e8e6) |
| **P2** | dense rectangular kernels (zero FMF default) + fact layer v1 with kill switches | the thesis test: per-fact-class A/B shows ≥1 win beyond the P0 clang lane at the §4 protocol, zero diff-oracle drift — **if not, stop; keep the clang lane and this document's inventory** | **LANDED** (0aba6ea, 37199f2); the gate's *thesis test* is NOT met — §0.3 measures parity, and the licensed-reassociation knob (available to both lanes) is worth about twice the backend choice |
| **P3** | `let rec` serial, halo, multi-accumulator; scan only if the §4 scan gate passed AND a real program shape exists | recursive-arrays corpus green both modes | **MOSTLY LANDED** (a6597fd): the `IRForRange` arm unblocked `let rec` and whole-rank folds; `recursive-arrays` joined the default differential sweep (7 pass, 2 named refusals: complex-valued, Int array literal). Halo and multi-accumulator ride the dense path. **Scan routing not started and not scheduled** — the §4 scan gate was never run, and the corpus still has no program whose recurrence is long *and* affine (max extent 6; the long ones are nonlinear) |
| **P4** | triangular/packed carriage; omp outlining via shim pool (or keep refusing omp → C++) | timing suite r! wins reproduced; outlined folds ≥ OMP lane | **STORAGE HALF LANDED** (4bf4234): rank-2 sym/antisym packed pools, triangular nests, blocked-simplex bricks, three-way agreement gate. omp still refuses to C++. Bricks measured at a 7% loss on MAPS (§0.4) and, once the fold was fused, a **1.12x win** on the row-operand shape the reuse hint targets — plan-simplex-blocked-compute.md §0, sixth block |
| **P5** | per-kernel hybrid DLL — demand-driven, explicitly skippable | a real mixed-construct program demands it | **NOT STARTED**; no demand yet |
| **Future (separate doc)** | **ORC-JIT lane for data-dependent shapes** — file-loaded extents, runtime SparseIdx cardinalities: the one residue AOT C++ provably cannot reach, and the only claim in this space with a real moat | provider-driven programs dominating profiles | untouched; still the only claim here with a moat |

**One deliverable the phasing did not anticipate — landed 2026-08-18:** an executable
cache for the llvm lane, in `compileLlvmProgram` (Build.fs). Same directory, same
`BLADE_EXE_CACHE` gate and eviction as the g++ lane; its own key material (clang
path/size/mtime stamp + normalized args + shim SOURCE text + `.ll` text, own version
tag) so the lanes cannot collide. Verified cold-store → warm-hit; the bench's codegen
table pins the cache off on both lanes, so the 4.69x stays an honest cold number.

### 0.7 A defect the benchmark found: kernel-body materializations are never freed

Emitting the gram fixture's IR and reading it (no runs needed) turned up the
cause of both the shape's throughput and, almost certainly, the day's host
crashes.

`reduce(x * y, (+))` over two row views **materializes the product**: the
emitted cell loop calls `blade_alloc_cells(402, 8)`, fills the buffer in one
402-iteration loop, then folds it in a second. That materialization is a
FRONT-END decision and both lanes make it — the C++ emission does the same
thing in `__lambda_25` — so it is a deferred-combinators decline
(docs/plans/plan-deferred-combinators.md, `tryInferReduceCompute`), not a
backend bug. It is what holds this shape to **0.44 GFLOP/s**.

What is a backend bug is what happens next. **The C++ lane deallocates the
temp before returning; the llvm lane never frees it.** That is not an
oversight — it is the arena model this file states at `src/EmitLlvm.fs:210`
("no deallocation (`nofree`: no emitted code ever calls `blade_free`)"), and
it is sound for materializations that happen *once per program*, which is
every shape the lane had been exercised on. A materialization inside a KERNEL
BODY breaks it: at n = 3001 the fixture allocates 4,504,501 buffers of 3,216
bytes and frees none, i.e. **~14.5 GB leaked in one ~8 s run** on a 15.4 GB
host. The `nofree` attribute the emitter stamps on those functions is
truthful, which is the tell.

That is the best available explanation for the crash history: memory
exhaustion, a shared-memory integrated GPU, and `VIDEO_MEMORY_MANAGEMENT_INTERNAL`
plus "paging file is too small" service failures. Note the llvm test suite ran
green repeatedly all day (348/0) — it was never generic load that was
dangerous, only this one shape.

Three fixes, cheapest first, none yet applied:

1. **`alloca` for literal-extent kernel-body temps.** 402 is a compile-time
   constant here, so the buffer can be a stack slot emitted in the function's
   ENTRY block (never in the loop body, which would grow the stack per
   iteration). LLVM then reuses one slot every iteration: no allocator traffic,
   no free needed, no leak, and almost certainly a large speedup. Needs a size
   ceiling so a big temp does not blow the stack, with the heap path as the
   fallback above it.
2. **Free what a kernel body allocated.** Track allocations made inside a body
   and emit `blade_free` at its end, dropping `nofree` from exactly those
   functions. Keeps the arena model everywhere else; more bookkeeping than (1).
3. **Do not materialize at all** — the deferred-combinators D-phase fix, which
   removes the temp rather than managing it, and helps the C++ lane equally.
   Strictly the best outcome and strictly the most work.

**RESOLVED 2026-08-18 (same day, commit 5a7b119) — by fix (3), the one that removes
the temp instead of managing it.** Two seams: `tryInferReduceCompute` now sees through
an anonymous `ExprCompute` at the operand root (both lanes fuse `reduce(A + B, (+))`
and `reduce(cos(A), (+))`), and the llvm lane's `emitReduce` consumes its operand —
`readCell` runs producers natively — forcing named operands only when the new
`ModuleFacts.ReadOnce` census shows a second reader (the IRLift operand hoist is
sole-read by construction, so kernel-body folds fuse). Measured on the gram fixture:
8.277 s → **0.331 s (25x)**, probe identical, per-cell allocation gone from the emitted
IR. Verified: llvm 348/0; C++ basic/functions/loops/units/math/index-types/sql all
green; interp diff basic/loops/units green. The paragraph below is retained as the
record of the exposure window; the arena model's remaining edge (a kernel-body
materialization that is NOT a sole-read fold operand) has since been closed from the
other side — fix (2)'s shape, generalized: function bodies and `IRForRange` trips are
pool-tracking scopes whose exits free what they allocated (see §0.8 item 3). Fix (1)'s
alloca ceiling remains unbuilt and would now be a pure allocator-traffic optimization,
not a leak repair.

Until one lands, the llvm lane should be considered unsafe for any program
whose kernel body materializes an array, and the gram fixtures should not be
run on a memory-constrained host. `blade test llvm` is unaffected: its corpus
materializes at program scale, which the arena model handles correctly.

### 0.8 The sibling audit — three read-only sweeps after the leak (2026-08-18)

Having found one instance of a bug class, three agents swept the lane for the rest:
allocation sites in repeated scopes, force-where-consume-would-do, and the soundness of
every fact asserted to LLVM. Two real defects fell out and are FIXED; the rest is a
worklist. Verdicts marked *verified* were checked by emission or execution, not by
reading.

**FIXED — a regression the fusion commit introduced** (`ab08399`, corpus loops/193).
`ReadOnce` consumption skipped module bindings, and forcing is what registers a deferred
binding for auto-print — so a module-level `let c = A <@> k` read exactly once by a fold
lost its output line entirely (`loops/129` covers the same shape with three readers,
which is why the suite missed it). The first repair — force module bindings always —
produced *"Instruction does not dominate all uses"*, because this fold can be emitted
inside a loop nest and the pool would not dominate the print reading it afterward.
`materializeExpr` bundles storage and printability; only the second is wanted here, so
`markForced` is called without materializing and the printer re-runs the producer at
module scope. *Verified: both lanes byte-identical; gram fusion intact.*

**FIXED — `norecurse` asserted about genuine cycles** (`b8906d7`, corpus functions/119).
Recursion was computed as a self-call test, blind to longer cycles — and those are
reachable, because a nested function sees the enclosing name (`function f = { function
h(m) = … f(m-1) …; h(n) }` is a legal f→h→f cycle; only *sibling* mutual recursion is
rejected). Both functions were emitted `mustprogress norecurse willreturn`: a
one-live-frame license plus a termination claim nothing proved, and silent. Now the call
graph's transitive closure decides, so inlined kernels need no special case. The same
patch iterates *distinct* callable records — the table maps alias keys to shared records,
so aliased bodies were scanned twice, which is fatal for a read census. *Verified: f and
h lose the attributes, `main` keeps them.*

**OPEN, confirmed by reading — ranked.**

1. **FIXED — antisym diagonal read one cell past the pool** (589d21b). `canonRead`'s
   strict-offset clamp discarded the *value* by select but performed the load
   unconditionally; at `i = j = n-1` the last row of a strict triangle is empty, so the
   offset equals `C(n,2)` — exactly the cardinality. The shim's 64-byte rounding absorbs
   it unless `C(n,2)·8` is already a multiple of 64 — smallest case **n = 16**. The
   guard landed first as a branch around the load, then went BRANCHLESS the same day
   (6246786): the vectorization census showed the branch defeating if-conversion on
   every antisym mirror-read loop (`CantVectorizeInstruction`), so the storage
   coordinates now REDIRECT to cell 0 on a diagonal — always inside the allocation,
   the shim's minimum pool being one 64-byte line — and the fetched value is discarded
   by select; branch vs select measured at runtime parity (19.8 vs 20.1 ms interleaved
   at n = 2003), and the select form vectorizes under the licence exactly as the sym
   path does. The same rewrite composes several strict groups correctly (zero if ANY
   group is diagonal, sign an XOR across swapped groups) where the old selects kept
   only the last group's flags. Pinned at the boundary corner by corpus symmetry/038
   in both lanes.
2. **FIXED — licensed FMF decorated the whole inlined fold kernel** (589d21b), not just
   the accumulate: `withFoldFmf` wraps `applyKernel`, which inlines a lambda kernel's
   body, so a kernel like `a + b * b` got `reassoc nsz` on its `fmul` — arithmetic no
   license covers. New predicate `foldFmfDecorable`: FMF decorates the application only
   when it IS the combining instruction — a `foldKernelBuiltinOp`-recognized builtin,
   where `applyKernel` emits exactly one `fadd`/`fmul`. The bench's licensed-fold win
   uses `(+)` and keeps its flags; comm-declared lambda kernels fold unflagged (and
   still correctly).
3. **MOSTLY FIXED — the arena's repeated-scope leaks** (scoped frees, this branch).
   Function bodies and `IRForRange` trips are now POOL-TRACKING SCOPES: every
   `allocPool` inside one records into a null-initialized tracking slot, and the
   scope's exit loads each slot and calls `blade_free` — a null slot (allocation on an
   untaken branch, or an escaped pool) frees nothing, so no dominance argument is
   needed for conditional allocations. Escapes null their slot (`keepPool`): a pool
   upgrading a binding that PREDATES the scope (the `KnownIds` snapshot) outlives it,
   and an array-returning function spares its result (textually — a tracking slot only
   ever holds its own allocation or null). A FreshPool callee's RETURNED pool is
   tracked at the CALL SITE like a local allocation — the fact is
   `ModuleFacts.FreshReturns`, this lane's own fixpoint mirroring the C++
   `computeFreshReturnFacts` but WITHOUT its interior-view arm (the C++ return arm
   copies a returned slice; this lane returns the GEP itself, so a view return is
   never caller-owned — and the arm must be dropped transitively, or `f() = g()` over
   a view-returning `g` would still classify fresh). Measured on a 1001×20001 rank-2
   `let rec` whose step calls an array-returning function: peak working set
   309.4 → **156.8 MB** — the trajectory's own 160 MB, the per-step leak gone; values
   byte-identical across lanes. Remaining conservatisms, both leak-shaped and bounded,
   never unsound: an unproven-fresh return stays untracked (leaked, never freed — the
   C++ facts' own one-sided rule), and a function whose own result has unknown
   provenance (a returned view, a branch-join load) frees none of its frame.
   `nofree` drops from the function attribute groups the moment any scope frees
   (`Ctx.AnyFrees`); the shim's `blade_free` is null-safe by contract.
4. **FIXED (two of three) — gratuitous forces**: `emitProdSum` consumes its operands
   (7633893) and `IRIndex` consumes an anonymous or sole-read deferred base (5abf494),
   both by the reduce fix's three-way rule. REMAINING (perf only): the rename arm
   copies unconditionally where the C++ lane elides via `canAliasStagingLet` when the
   source solely owns a fresh pool — an llvm twin needs the enclosing let-chain
   context, and the copy is safe, merely duplicated.
5. **FIXED — brick reassociation is knob-gated** (589d21b). The blocked-simplex fold
   bricked on the structural license alone; a brick walk reassociates the RESULT, so it
   now takes the same two-part gate as FMF (`foldFmfLicensed`: license AND
   `BLADE_FP_REASSOC`), closing the seam before BL3999's refusal ever lifts.

**Predictions that did NOT reproduce**, recorded so nobody re-chases them: two proposed
gram variants (a named-`let` operand, and a lambda fold kernel whose seed error declines
the terminal) were predicted to leak per cell. *Verified by emission: both produce
exactly two pools, neither per-cell.* The typecheck unwrap and the sole-read consumption
between them already cover those shapes.

**Verified SOUND** (checked hard, worth not re-auditing): `align 64` holds on every shim
path including overflow and OOM, which both panic rather than return; `readonly` on array
params — every emitter-internal store writes a pool allocated in the same expression, and
the one store that can reach a parameter is exactly the node the scan matches; `noalias`
on fresh pools; and the deliberate placement of cell reads outside the licensed FMF
scope. Also structurally reassuring: every whole-module scan recurses through a *total*
active pattern with no catch-all, so "the scan missed a node kind" cannot happen silently.

---

## 8. Risks

1. **Miscompile exposure from alias facts.** Rust's calibration: mutable-noalias
   introduced 2017, default 2021, regressed immediately, still an unstable flag six
   years on. Blade's provenance is *weaker* than borrowck in one concrete way:
   `MutParamPositions` is name-keyed with a documented shadowing weakness
   (TypeEnv.fs:239, and the sibling map at :257 that shares the weakness) — today a
   diagnostics bug, under `noalias` emission a
   miscompile. **Fix the keying (name → IRId) before any emission trusts it**; emit
   `noalias` on output pools only at first; full `--interp --diff-oracle` sweep per LLVM
   point release; kill switches.
2. **Pipeline behavior on unusual-but-valid IR.** `clang file.ll -O3` runs the C
   pipeline on IR no C front end produced; reduce-heavy shapes are precisely where
   optimizer coverage is thinnest (Polly's own literature: no production polyhedral
   optimizer handles reductions well).
3. **Third-backend maintenance against a byte-pinned corpus.** 1878 corpus files; WARN
   pins strict both directions; a third lane must reproduce everything `src/cpp/`
   supplies (4 storage classes, 22 BLAS routes + 8 LAPACK entries, rand, orbit/wreath,
   providers, display formatting). Whole-program refusal keeps coverage binary; the
   emission-text tests (OmpTests/LinAlgTests/HybridTests assert C++ pragma text) need
   explicit backend scoping or LLVM twins.
4. **Three host toolchains** (g++, cl.exe-for-nvcc, clang) with a documented interaction
   bite (`__CUDA_ARCH__`-first macro ordering); `blade_portability.hpp` remains the
   single spelling authority.
5. **Header contract → linkage contract.** BLAS/LAPACK shims are `#include`d today; the
   LLVM lane calls them by symbol against a fixed ABI, an area with two documented
   silent-staleness traps already.
6. **Print/EXPECT byte identity** — the likeliest early red; run full EXPECT sweeps from
   P1, never a curated subset.

---

## 9. Coexistence with the MLIR plan — ownership split

The audit flagged five seed claims double-counted between the two documents. The split,
so each win is billed once:

- **MLIR doc owns** representation-level wins: fusion machinery (multi-result
  `linalg.generic`), tiling/interchange as reusable passes, parallel constructs
  (`scf.forall`, omp dialect — the "optimizer transforms through the license" claim
  lives there), the layout-algebra pass (its §6), GPU (`cuda_tile`), and — booked
  nowhere until built — scan routing's transformation half.
- **LLVM doc owns**: the toolchain/compile-time argument (R6), the residual fact
  channels (R1/R2/R4), carriage of non-affine SymIdx kernels the MLIR path refuses
  until a `blade` dialect exists, and the future ORC-JIT lane.
- **Shared, built once**: `BackendFacts.fs`; the `FoldWrapper`/`ShareDecl` typed-primary
  refactor; the C shim; the flat-pool ABI (`DeviceBufferType` as ground truth, memref
  descriptor as its dressed form); the probe/gate/doctor/standalone-test-block/golden-
  pin template (CUDA → LLVM → MLIR).
- Structural relationship: parallel siblings in tooling; de facto stacked in substance —
  MLIR lowers through LLVM IR regardless, so the LLVM lane is the bottom half of the
  MLIR path without ever being a code dependency of it.

---

## 10. Repo corrections surfaced by this research

1. `docs/plan-cpp-perf-exploitation.md` was deleted at ef94c50 but is still cited by
   docs/plans/plan-static-array-erasure.md:79,318,386 and five source comments
   (IRMono.fs:1623, CodeGenExpr.fs:2377, CodeGenExprSupport.fs:1070, Diagnostics.fs:266,
   ParserPatterns.fs:183); recover via
   `git show a2d8b4d:docs/plan-cpp-perf-exploitation.md` (2967 lines). Restore or
   repoint (tracked as a separate task). *Update 2026-08-18: CLAUDE.md no longer cites
   it — the reorg sweep removed that one; the rest stand.*
2. Mutual recursion's diagnostic is BL4006 (Diagnostics.fs:233); BL2001 is "unbound
   variable" — the symptom form. CLAUDE.md's shorthand conflates them.
3. Main-local function references emit as `std::function` locals — the closed world is
   currently spent at emission. Census the emitted corpus for hot-loop occurrences
   (predicted near zero); if any, pass captures as explicit parameters — an F# fix worth
   doing on the current backend.
4. ~83/472 surveyed loop headers still load `.extents[d]` where a literal exists —
   the S-effort residue of the 1.77x, listed sites recoverable from the deleted doc.
5. `MutParamPositions` name-keying (risk 1) should be fixed regardless of this plan.
