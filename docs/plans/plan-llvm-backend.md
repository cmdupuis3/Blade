# Direct LLVM backend — tiered optimization inventory and design

**Status: EXPLORATORY — and the headline thesis did not survive its own audit intact.**
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
cited by CLAUDE.md, docs/plans/plan-static-array-erasure.md:82, and src/IR.fs:2502). This
document does not overturn that verdict; it narrows the LLVM case to the residual set and
gates everything behind a one-day experiment (§4) that requires zero backend code.

---

## 1. Evidence base

**Banked already, through the C++ text channel** (no backend change involved — these
discipline every "LLVM would unlock X" claim):

| win | mechanism | measured |
|---|---|---|
| flat-pool elementwise mode | frontend picks flat traversal; g++ fully unrolls the 12-trip inner loop | 2.0x at 1000x12 |
| per-site FP reassociation | `omp simd reduction` / K-lane forms, license-gated ([CodeGen.fs:2607](../src/CodeGen.fs) `foldReorderLicensed`, `fpReassocEnabled` :491) | 1.64x dot, 2.60x gemv fiber, 3.36x 3-stream former |
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
(`BLADE_MEMCHECK`, Build.fs:382-417); no `mlir-*` tool exists — resolving the MLIR plan's
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
(SymcomState IR.fs:1619-1638, `computeTriangularBound` :1713, exact r! speedup computed
as a number :1701-1710); antisym stores no diagonal, with `StrictOffset` confined to flat
reads (CodeGen.fs:5928-6012); symmetric output storage is licensed only under proven
same-array occupancy (`IRGram(_,_,isSameArray)` IR.fs:150 — a C compiler can never
legally assume A≡B), and the refusal is proved correct. Iteration *and* storage shrink by
r!. No surveyed system derives triangular iteration from a type — Polly can optimize a
hand-written triangular loop but cannot invent one, and the packed offset (a sum of
binomial coefficients) is non-affine: outside polyhedral reach, outside `linalg`, outside
CuTe's multilinear layout algebra (plan-mlir-backend.md:151-155). Family members: sign
parity proofs (`KernelSignParity` proven from the body, carried on
`IRCallable.SignParities`, consumed by the wreath-tie soundness gate IR.fs:2117-2140) and
the omp-vs-BLAS routing table's syrk rationale (LinAlgPatterns.fs:491-531).
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
(TypeCheck.fs:14176-14192) classifies prefix reads into `n`, `n±c`, `const` with exact
compile-time lags. That hands over, for free, the half of the scan-synthesis problem
that defeats every C/Fortran auto-parallelizer (dependence recovery from pointer
arithmetic) — automatic recurrence→scan has *no production precedent*; the research
frontier is one month old (ScanWeaver, arXiv 2606.00601). But the audit's corrections
are decisive: (a) the recognizer covers exactly four index shapes — anything else falls
to guarded-unknown; (b) nothing proves the step linear or associative — the only
associativity machinery is a builtin-op recognizer (CodeGen.fs:2549,2588), and building
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
| T2.1 | Per-site FP reassociation licenses | `foldReorderLicensed` (CodeGen.fs:2607) = declared `comm` ∨ groups ∨ recognized builtin body, cross-checked against body parity; BL4016 refuses unlicensed parallel folds | Halide *proves* associativity (stronger leaf); C/Fortran have TU-wide flags or unchecked pragmas | **Delivered** via `omp simd reduction`/K-lanes (1.64–3.36x); clang pragma closes the rest (probe P4). LLVM adds per-*instruction* granularity no Blade license demands — capability without demand |
| T2.2 | Deterministic licensed reassociation | fixed K-lane accumulation shared across all emitters so the answer can't drift between sites (CodeGen.fs:2639-2787) | `-ffast-math` destroys run-to-run determinism; no surveyed system offers "canonically reassociated" | **Delivered.** A genuinely unusual property worth advertising: reassociated *and* reproducible |
| T2.3 | No-versioning-scaffold codegen (the correct framing of static extents) | shape monomorphization bakes literal trip counts; measured 1.77x on a short fiber, mechanism = the eliminated runtime-remainder scaffold (55→25 instructions) | gcc/clang version loops at runtime for alias/alignment/trip; Julia/XLA specialize per signature (equal); Futhark size types (equal) | **Delivered in principle; 83 of 472 surveyed loop headers still load `.extents[d]`** — an S-effort C++ emitter fix (route through `literalOrRuntimeExtent`), no backend content |
| T2.4 | Extent-agreement proofs | BL3016 at both seams (Unify.fs:128, TypeCheck.fs:12443) + halo twin + runtime BL8009 | nothing checks this statically in C-family; Futhark size types equal | **Delivered**; literal-only — runtime equalities are assumables (`llvm.assume icmp eq`), not provables |
| T2.5 | Guard-free indexing via index provenance | `Nat<LatIdx>` ≠ `Nat<LonIdx>` at equal extent; no bounds checks *exist* to eliminate; index-tag arithmetic forbidden by design | Rust: checks-then-hope-LLVM-deletes; Julia `@inbounds` = unchecked UB; Futhark/XLA reach the same place | **Delivered** backend-independently; provenance is erased by IRProgram, so there is nothing left to assert to a backend — `!range` on loop counters is redundant with literal bounds |
| T2.6 | Deferral-driven buffer assignment / in-place update | whole pipeline dataflow known before any buffer chosen (deferred until `IRCompute`); `mut` array params the only aliasing channel; escape analysis trivial (`collectFreeVars` exhaustive) | XLA buffer assignment + Halide storage folding ship *better* machinery today; Futhark uniqueness types = checked in-place licenses | **The largest measured headroom in the memory family** (LSWOSA serial 1.35x deficit vs classic C attributed to temp allocs). An F# transform + `llvm.lifetime` markers; not backend-gated. **Lead the memory story with this, not noalias** |
| T2.7 | Typechecked parallel folds | `omp(x: n)` depth caps; BL4016; `OmpRequested` never silently dropped; FoldChunkPlan's defined partial order (IR.fs:2501-2523) vs OpenMP's unspecified combine | rayon is checked (equal-ish); OpenMP/Julia unchecked; Futhark auto but operator associativity unchecked | **Delivered** to `#pragma omp`. The "optimizer transforms *through* the license" upgrade is MLIR's (`scf.forall`), not LLVM's — LLVM has no parallel construct; `!llvm.loop.parallel_accesses` is a vectorizer fact, and hand-emitting `__kmpc_*` would trade a stable pragma surface for a version-pinned private ABI |
| T2.8 | Symmetry-derived schedule policy | triangular imbalance ⇒ `schedule(dynamic)` derived, not chosen (CodeGen.fs:6261) | OpenMP users hand-pick; Halide autoschedules (better ceiling); no annotation system has the structural fact | **Delivered.** A family the seed list missed entirely |
| T2.9 | Typed-algebraic BLAS/LAPACK routing | routing from `gram`/`prodsum` algebraic forms + identity groups, policy as one literal table with rationale (LinAlgPatterns.fs:491-531) | ICC pattern-matches loops (fragile); Julia dispatch, XLA routing (comparable-to-better coverage) | **Delivered and banked** (syrk symmetric wins measured). Stays out-of-band on any backend; `llvm.matrix.*` is not a BLAS mechanism |
| T2.10 | Layout ownership | no source-declared layout; padding/alignment/SoA legal where C's ABI forbids it | Halide storage directives + XLA layout assignment ship today; Rust reorders fields only | **Unbuilt; measurement-gated** (2.14x packed regression is the standing warning; pad-the-stride is 2^k-only per the 1.12–1.26x intrinsic figures). Allocator + nest builder are F#; zero backend content |
| T2.11 | Whole-language compile-time evaluation | StaticEval incl. static provider payload folding (StaticEval.fs:59-64); extent exprs fully folded | Julia `@generated`/Zig comptime at least equal; C++ constexpr weaker | **Delivered** (mechanism tier 3, coverage tier 2). Emit as constants + `unnamed_addr`; don't double-bill with T2.3 |
| T2.12 | Rank≥2 recurrences: serial-outer/parallel-inner by construction | whole-slice steps desugar to elementwise trailing-axis nests (TypeCheck.fs:14136-14155) | wavefront legality is a polyhedral research result elsewhere | **Delivered** shape; inner-loop parallelism annotation is C++-spellable |
| T2.13 | Group-by/ragged as CSR by construction | `IRGroupKeys`/`IRGroupSizes`/`IRRaggedLookup` (IR.fs:121-123,198) | hand-written CSR elsewhere; MLIR sparse_tensor comparable | **Delivered**; segmented-reduction lowering possible on any backend |
| T2.14 | Mask/compound branchless selection | selection materialized once, not per-iteration branching (IRMask IR.fs:99) | predication via `llvm.masked.*`/VP intrinsics is the LLVM spelling | **Delivered** semantically; gather-heavy code is bandwidth-bound — marginal |

### TIER 3 — general-purpose good practice (emit, don't advertise)

Complete alias map as *hygiene*: `memory(argmem: read)`/`memory(none)`, per-param
`noalias readonly writeonly align captures(none) noundef`, scoped `!alias.scope` domains
— the capability is real and total (`MutParamPositions` TypeEnv.fs:233 is the only write
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
(CodeGen.fs:1388-1408; corpus witnesses functions/023, functions/086). Mutual recursion
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
(IR.fs:2591-2601) is the shared ground truth, and the memref-descriptor form the MLIR
plan needs falls out of the same layer.

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
assert. Per-kernel hybrid later over the proven nvcc-DLL shape (Build.fs:262-316).
`BLADE_LLVM` gate (function), `HasClang` capability probe (skip-not-fail),
`blade test llvm` standalone block, golden emission pins + EXPECT execution + llvm-vs-
interp diff on dense categories. Per-fact-class kill switches
(`BLADE_LLVM_FACTS=noalias:off,...`) so any miscompile bisects to a fact class in one
run.

---

## 6. Numeric policy (the licensed path is not the tested path)

Adopted from the existing contract (CodeGen.fs:418-495) unchanged: (i) `reassoc nsz`
FMF only where `foldReorderLicensed` ∧ `fpReassocEnabled()`; (ii) `test interp` /
`diff-oracle` run with reassoc and contraction off, byte-for-byte, no float tolerance
ever enters InterpDiff — that gate has caught every real divergence; (iii) the licensed
path is tested by a separate block whose corpus uses integer-valued f64 (exact under any
association — the standing rule for parallel-reduce tests) plus invariant-based checks
(orthogonality residuals, reconstruction error) as the math corpus already does. Scan
emission, if built, sits behind the same license and the same split.

---

## 7. Phasing

| phase | deliverable | gate |
|---|---|---|
| **P0** | clang-lane A/B (§4) — no backend code | decision rule §4; also settles Windows toolchain reality (already resolved favorably by P1/P2 probes) |
| **P1** | scalar-program `.ll` emission end-to-end: EmitLlvm skeleton, shim, `BLADE_LLVM` gate, doctor row, golden pins, `blade test llvm` | scalar + dense corpus subset byte-identical to interp; `, N skipped` audited |
| **P2** | dense rectangular kernels (zero FMF default) + fact layer v1 with kill switches | the thesis test: per-fact-class A/B shows ≥1 win beyond the P0 clang lane at the §4 protocol, zero diff-oracle drift — **if not, stop; keep the clang lane and this document's inventory** |
| **P3** | `let rec` serial, halo, multi-accumulator; scan only if the §4 scan gate passed AND a real program shape exists | recursive-arrays corpus green both modes |
| **P4** | triangular/packed carriage; omp outlining via shim pool (or keep refusing omp → C++) | timing suite r! wins reproduced; outlined folds ≥ OMP lane |
| **P5** | per-kernel hybrid DLL — demand-driven, explicitly skippable | a real mixed-construct program demands it |
| **Future (separate doc)** | **ORC-JIT lane for data-dependent shapes** — file-loaded extents, runtime SparseIdx cardinalities: the one residue AOT C++ provably cannot reach, and the only claim in this space with a real moat | provider-driven programs dominating profiles |

---

## 8. Risks

1. **Miscompile exposure from alias facts.** Rust's calibration: mutable-noalias
   introduced 2017, default 2021, regressed immediately, still an unstable flag six
   years on. Blade's provenance is *weaker* than borrowck in one concrete way:
   `MutParamPositions` is name-keyed with a documented shadowing weakness
   (TypeEnv.fs:225-233) — today a diagnostics bug, under `noalias` emission a
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
   CLAUDE.md, docs/plans/plan-static-array-erasure.md:82, and src/IR.fs:2502; recover via
   `git show a2d8b4d:docs/plan-cpp-perf-exploitation.md` (2967 lines). Restore or
   repoint (tracked as a separate task).
2. Mutual recursion's diagnostic is BL4006 (Diagnostics.fs:233); BL2001 is "unbound
   variable" — the symptom form. CLAUDE.md's shorthand conflates them.
3. Main-local function references emit as `std::function` locals — the closed world is
   currently spent at emission. Census the emitted corpus for hot-loop occurrences
   (predicted near zero); if any, pass captures as explicit parameters — an F# fix worth
   doing on the current backend.
4. ~83/472 surveyed loop headers still load `.extents[d]` where a literal exists —
   the S-effort residue of the 1.77x, listed sites recoverable from the deleted doc.
5. `MutParamPositions` name-keying (risk 1) should be fixed regardless of this plan.
