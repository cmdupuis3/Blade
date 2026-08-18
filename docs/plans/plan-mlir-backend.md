# LLVM/MLIR codegen backend — design sketch

**Status: EXPLORATORY. Nothing is scheduled; no code exists.** Written 2026-08-17,
following the CAKE / CUDA Tile IR survey (CAKE — arXiv 2608.12629 — has no public
IR surface and cannot be targeted; NVIDIA's targetable tile stack is the
`cuda_tile` MLIR dialect, github.com/NVIDIA/cuda-tile, Apache-2.0). This document
sketches what a real MLIR backend for Blade would look like, where CuTe-style
layout algebra would land in the type system, and in what order the work would
have to happen. Everything here is a proposal, and several claims are marked
VERIFY where they rest on un-checked toolchain facts.

Ground rules the sketch takes as fixed:

- The interpreter (`src/Interp/`) stays the semantic oracle. Any new backend is
  a third differential twin, held to the same corpus.
- The C++ backend stays canonical and default. MLIR is opt-in and gated exactly
  like CUDA: env gate + capability probe + standalone test block, skip-not-fail.
- Refusals are features. The backend refuses what it cannot express and falls
  back to the C++ path, mirroring `cudaDeviceBodyRefusal`
  (`CodeGen.fs:10103-10113`) — never a half-emitted kernel.

---

## 1. Why MLIR at all

1. **GPU without hand-SIMT.** `cuda_tile` consumes MLIR (textual or
   programmatic) and lowers `cuda_tile → nv_tileaa → nv_tileas → NVVM → LLVM →
   SASS`. Its model — logical threads over tile fragments of tensors — is far
   closer to Blade's loop objects than the `__global__`-per-kernel path in
   `genCudaKernel` (`CodeGen.fs:11220`), and it dissolves the current
   device-body scope wall (separate `.cu` translation unit, capture forwarding
   by parameter, host-fallback refusals) because host and device live in one IR
   until late lowering.
2. **The linalg fusion/tiling machinery.** Blade already fuses pipeline stages
   (`>>@`, `IRFusion`) and derives iteration order in `buildLoopNestCodeGen`
   (`IR.fs:3349`). MLIR's `linalg`-on-tensors gives the same moves —
   elementwise fusion, tiling, loop interchange — as reusable passes instead of
   21.5k lines of bespoke emission.
3. **Retiring the MSYS2 dependency (eventually).** Today every compiled program
   needs ucrt64 g++ on PATH. A full-MLIR host path (§7, Phase 5) would emit
   object code through LLVM directly. This is the endgame, not the deliverable.
4. **Blade's static-extent regime is a gift to MLIR.** Ranked tensors with
   static shapes are MLIR's happiest case, and Blade knows extents statically
   far more often than most frontends (`Idx<N>` literals bake bounds; the
   recursion axis of a `let rec` must be static).

What MLIR does *not* buy: any notion of symmetric/triangular storage,
commutative-fold licensing, or hashed index spaces. Blade's structure
exploitation stays Blade's (§5).

---

## 2. Bind point: how the F# compiler talks to MLIR

Three options; the first is recommended.

| option | mechanism | verdict |
|---|---|---|
| **(a) textual emission** | new `src/EmitMlir.fs` emits `.mlir` text; `Build.fs` drives `mlir-opt` / `mlir-translate` / `clang` as external tools | **Recommended.** Same skill and discipline as C++ emission; byte-pinnable golden files; no FFI; toolchain gated like nvcc |
| (b) MLIR C API via P/Invoke | build IR in-process against `libMLIR-C` | Faster round-trips, but a native-interop surface pinned to one LLVM build, painful on Windows, and it couples compiler-process lifetime to LLVM. Not first |
| (c) small C++ driver binary | F# serializes a private format; a C++ tool builds MLIR | Worst of both — a second codebase and a second serialization. No |

Emission style for (a): follow `EmitCpp.fs`'s reason for existing — typed
builders per op (`linalgGeneric`, `scfFor`, `tensorExtractSlice`), never
positional `sprintf` (the argument-transposition bug class `EmitCpp.fs:1-10`
was created to kill). One builder per MLIR op keeps the textual format
auditable.

Toolchain integration copies the CUDA template exactly:

- `Capabilities` gains `HasMlir` (probe `mlir-opt`/`mlir-translate`/`clang` the
  way `nvccProbe` works, `Build.fs:183-184`); missing toolchain ⇒ tests skip.
- Env gate `BLADE_MLIR` (`1`/`on` routes eligible programs; unset/`0` = C++),
  read per-call as a function, never a module-level `let` (the pin/restore
  discipline CLAUDE.md calls out for `Build.fs`/`CodeGen.fs`).
- `blade doctor` gets a toolchain row (version, path, staleness).
- `blade test mlir` is a standalone block like `blade test cuda`
  (`Cli.fs:2829-2834`), excluded from the default suite.

**VERIFY:** the official LLVM release binaries for Windows do not ship
`mlir-opt`/`mlir-translate`. Options to check in Phase 0: build and vendor a
pinned MLIR toolchain (toolchain.json already exists for exactly this kind of
packaging), or ride the MLIR/LLVM stack that `cuda-tile` bundles. The plan pins
ONE LLVM version; textual MLIR is not stable across major versions and golden
`.mlir` pins must be against our emission, never against `mlir-opt` output.

---

## 3. Where emission forks from the existing pipeline

`IRProgram` — the output of `Lowering.lower` (`Lowering.fs:2381`) — is the fork
point. It is provably target-neutral: it is exactly what the interpreter
consumes, and grepping `IR.fs` finds no embedded C++ (every `std::` hit is a
comment). All current target decisions (OMP pragma placement, BLAS routing via
`LinAlgPatterns.resolveNodeRoute`, CUDA extraction) happen inside `CodeGen.fs`
at emission time, so nothing upstream needs to move.

The reusable interior boundary is `LoopNestCodeGen` (`IR.fs:2525-2578`) — the
per-kernel loop plan (bindings, kernel expr, output type + symmetry vector,
captures, fold/share metadata) already consumed by three emitters (host nest
`CodeGen.fs:7604`, CUDA `CodeGen.fs:10734`/`11220`, BLAS active patterns
`LinAlgPatterns.fs:1021/1115/1271`). `EmitMlir.fs` becomes its fourth consumer.
Two of its fields are C++-flavored strings (`FoldWrapper`, `ShareDecl`); the
MLIR consumer must regenerate those from the typed fields rather than splice
the strings — worth a small refactor that makes the typed form primary.

New file placement: `Blade.fsproj` has `EnableDefaultItems=false` and compile
order is dependency order — `EmitMlir.fs` slots after `Lowering.fs`, beside
`EmitCpp.fs`, and needs only `IR.fs` + `Lowering.fs` upstream.

---

## 4. Dialect mapping — stock dialects first

Phase 1–3 target only stock dialects (`func`, `arith`, `math`, `scf`, `tensor`,
`linalg`, `bufferization`, `memref`, `index`). No custom dialect until §5 earns
one.

| Blade IR | MLIR | notes |
|---|---|---|
| `IRMethodFor(A, B)` outer product | `linalg.generic`, parallel iterators, broadcast indexing maps | output rank = sum of operand ranks, exactly the affine-map product |
| `method_for(zip(A, B))` co-iteration | `linalg.generic` (or `linalg.map`), identity maps | the outer-vs-zip distinction becomes literally visible in the indexing maps |
| `IRReduce` with `comm`/reassoc license | `linalg.reduce` (unordered) | licensed reordering only — see fold-order caveat below |
| `IRReduce`, non-commutative kernel | `scf.for` sequential fold, innermost axis | Blade guarantees right-to-left innermost-first; `linalg.reduce` guarantees nothing. Unlicensed kernels MUST take the ordered lowering |
| `reduce(... , axes = k)` partial fold | `linalg.reduce` over the k innermost dims | rank-typed already, clean match |
| multi-accumulator `<&!>` one-pass reduce | one multi-result `linalg.generic` | the "several statistics in ONE pass" idiom is a native concept here — a genuinely better fit than the C++ emission |
| `object_for` pipelines, `>>@`, `IRFusion` | compose `linalg` ops on tensors; run elementwise-fusion pass | deferral-until-`compute` maps to tensors being SSA values; bufferization happens once, at the end |
| `let rec` recursive arrays | `scf.for` with `iter_args` carrying the tensor, `tensor.insert_slice` per step | structural induction on the leading axis with a static bound is precisely `scf.for` + loop-carried value; the implicit-zero prefix read becomes an `arith.constant` splat init |
| halo windows | `tensor.extract_slice` + `tensor.pad` (zero) | implicit-zero out-of-range reads = pad semantics; the BL3016/BL8009 extent guards stay front-end |
| `range<Idx<N>>` virtual arrays | no materialization; `linalg.index` inside the consuming generic | strictly better than the C++ path's materialization decisions |
| `IRCompute` / materialization forms | `bufferization` boundary | one-shot bufferize replaces the 14 hand-written `materialize*Form` builders and their extents-lifetime traps |
| static extents | static `tensor<4x8xf64>` shapes | the common case |
| runtime extents (generic fns) | dynamic dims `tensor<?x?xf64>` + `index` args | mirrors `CellsRuntime` in the CUDA path |
| `prodsum` / `gram` L3 contractions | `linalg.matmul` + named ops; or keep `LinAlgPatterns` BLAS routing as `func.call` | `syrk` has no named linalg op — gram's symmetric-output storage stays on the C++/BLAS path initially |
| units, index provenance (`Nat<LatIdx>`) | erased | both are front-end typecheck constructs; by `IRProgram` they are already discharged |
| `where` licenses (`comm`, `omp` depth, block size) | discardable attributes on the emitted ops | consumed by OUR pass-pipeline configuration, ignored by upstream passes |

The fold-order row is the one semantic trap in the whole table. `reduce` folds
right-to-left, innermost first, and corpus pins depend on it; `linalg.reduce`
is unordered by construction. The license split (`IsCommutative` /
`BLADE_FP_REASSOC` ⇒ unordered; otherwise ordered `scf.for`) must be enforced
in the emitter, and byte-identity with the interpreter — the same "off unless
licensed" philosophy as `BLADE_BLAS` — is the default.

---

## 5. What stock MLIR cannot say — and the eventual `blade` dialect

Three storage regimes have no stock representation:

1. **`SymIdx`/`AntisymIdx` triangular packing.** The packed offset is a sum of
   binomial coefficients — polynomial in the indices, therefore not an affine
   map, and (note) not a CuTe layout either: CuTe layouts are multilinear
   shape/stride maps, and triangular packing is fundamentally outside that
   algebra. Options, in order:
   - *(initial)* refuse → the whole kernel stays on the C++ backend;
   - *(Phase 4)* a small `blade` dialect: a `!blade.packed<sym, r, n>` type and
     a `blade.pack_offset` op lowering to `arith` index math over a flat
     `memref` — correct, loses affine analyzability, keeps the r! savings;
   - *(rejected)* mirror into dense — forfeits the language's core win;
   - *(specced 2026-08-17)* simplex-blocked decomposition — cover the simplex
     with dense bricks (off-diagonal tile blocks are constraint-free boxes) and
     recurse only the diagonal residue, shrinking this refusal from per-kernel
     to a 2^{-d} residue: `docs/plans/plan-simplex-blocked-compute.md`.
2. **`SparseIdx`/`CompoundIdx` hashed spaces.** The `sparse_tensor` dialect's
   level types (dense/compressed/singleton) model sorted compressed formats,
   not hash tables with wildcard reads. Refuse initially; `Ragged` is the
   exception — CSR-shaped, and `sparse_tensor`'s compressed level fits it
   almost verbatim (Phase 4 candidate).
3. **Ring/snapshot recursive-array storage** (the windowing branch): refusal
   until the dense `scf.for` path is proven.

The refusal predicate `mlirBodyRefusal` mirrors `cudaDeviceBodyRefusal`
structurally, but the fallback is cheaper: since the C++ backend still compiles
everything, refusal granularity in Phase 1 is **whole-program** (`BLADE_MLIR=1`
+ refused construct ⇒ diagnostic naming the construct, fall back to C++).
Per-kernel hybrid (MLIR-compiled kernels in a DLL called from the C++ host,
over the flat-pool ABI below) is a later refinement, not the starting point.
New refusal diagnostics follow the 5-touch-point BL-code protocol.

---

## 6. CuTe layout algebra in the virtual-array system

CuTe's contribution is not its C++ templates but the algebra: a **Layout is a
nested (Shape, Stride) tree** denoting an index→offset function, closed under
composition, product (tiling), division (partitioning), and complement. Blade
has the *semantic* half of this already — an `Array<T like I, J>` **is** the
function `I → J → T` — but the *offset* half is implicit today, buried in
Iliffe pointer skeletons, `CompoundIdx` flat-subscript math, and packed-storage
special cases. There is no layout pass (measured fact: none exists, and naive
packing was 2.14x *slower*, so this must be benchmarked, never assumed — and
never at power-of-two extents).

Proposal: make layout a **deduced attribute of concrete array types**, computed
in `Deduce` alongside symmetry classes — not user-facing syntax. One `Layout`
tree per concrete array unifies four things that are currently separate
mechanisms:

| today | as layout algebra |
|---|---|
| Iliffe nesting / row peel (`A.data[i0], A.extents+1`) | layout **slicing** — partial indexing = fixing a mode, which is dimensional currying made computational |
| `CompoundIdx` flat `B(lat, lon)` subscripts | layout **coalescing** of two modes into one |
| GPU tiling / fragment assignment | layout **division**: iteration space ÷ tile layout = per-tile coordinates — literally the operand format `cuda_tile` wants |
| `memref` strided descriptor | the degenerate flat (non-nested) layout — i.e. the MLIR ABI in §7 falls out for free |

The honest limits: (a) `SymIdx` packing is outside the algebra (§5) — layout
deduction covers the dense/blocked/rectangular world and coexists with, never
replaces, combinatorial packing; (b) swizzles (the other half of CuTe) matter
only once the `cuda_tile` path is real, and Tile IR largely derives them
itself — do not build a swizzle calculus speculatively.

Sequencing: layout deduction is Phase 3's enabling work, not a prerequisite for
Phases 0–2 (which live on static shapes and default row-major strides).

---

## 7. Runtime, ABI, and the host question

- **Phases 0–2 (CPU, whole-program):** an MLIR-routed program never touches
  `nested_array_types.hpp` at all — tensors bufferize to flat `memref`
  descriptors `(allocated, aligned, offset, sizes[], strides[])`, and the
  Iliffe wrapper simply isn't emitted. Print/display, `blade_rt` frames, and
  providers are the boundary: Phase 0–1 programs link a thin C runtime shim
  (print + panic + alloc) instead of the C++ runtime headers.
- **Per-kernel hybrid (later):** MLIR-compiled kernels packaged as a DLL called
  from the normal C++ host, marshaling flat pool + extents into a memref
  descriptor — the same shape as the existing nvcc-DLL pattern
  (`Build.fs:262-345`) and `DeviceBufferType`'s pool contract (`IR.fs:2598`).
  This is how MLIR kernels and refused-construct C++ code coexist in one
  program.
- **Phase 5 (aspirational): full-MLIR host.** Everything `CodeGen.fs` emits —
  module init, scope teardown, shadow-call-stack frames, NetCDF/Zarr/CSV
  providers — reproduced over LLVM. This retires g++/MSYS2 and is a huge lift;
  it is listed so nobody mistakes the hybrid for the endgame, and deferred so
  nobody starts there.

---

## 8. Testing

- **Differential gates:** the interpreter stays the oracle. `blade test mlir`
  runs (a) golden emission pins on a small corpus subset (our `.mlir` text,
  pinned byte-for-byte), (b) execute-and-compare against interpreter EXPECT
  values, (c) an mlir-vs-cpp diff sweep on the dense corpus categories.
- **Numeric policy:** default lowering is order-preserving; `linalg.reduce` and
  any reassociating pass only under the existing licenses
  (`comm` / `BLADE_FP_REASSOC`), and FMA contraction mirrors
  `BLADE_FP_CONTRACT`. Byte-identity with the interpreter beats the last ULP.
- **Skips:** missing toolchain ⇒ skip, and the `, N skipped` suffix is checked
  before trusting green, as always.

---

## 9. Phasing

| phase | deliverable | proves |
|---|---|---|
| **P0** | toolchain probe + doctor row; `blade emit --mlir` for scalar-only programs (`func`/`arith`/`math`/`scf`); execute via clang | the bind point and version pin (VERIFY items in §2) |
| **P1** | dense rectangular kernels: `method_for`/zip/`reduce` → `linalg` on tensors, one-shot bufferize, LLVM CPU; `mlirBodyRefusal` + whole-program fallback; `blade test mlir` | the dialect mapping core and the license-gated fold-order split |
| **P2** | `let rec` (`scf.for` + `iter_args`), halo (`extract_slice`/`pad`), multi-result `<&!>` generics | the sequential-structure story |
| **P3** | GPU: `linalg` → `cuda_tile` (fallback: stock `gpu`/`nvvm`); layout-deduction pass (§6) feeding tiling | the reason this backend exists |
| **P4** | `blade` dialect for triangular packing; `Ragged` via `sparse_tensor` | structure exploitation off the host |
| **P5** | full-MLIR host | aspirational; explicitly not scheduled |

Each phase lands behind `BLADE_MLIR` with the C++ backend untouched and
default. The first go/no-go review is after P1: if the dense-kernel corpus
diff-gates green and the emitted code is within noise of the C++ backend on the
non-power-of-two benchmark protocol, P2+ proceeds.

---

## 10. Risks

1. **LLVM/MLIR distribution on Windows** — the biggest unknown (VERIFY, §2);
   toolchain.json packaging is the intended mitigation.
2. **Textual-IR churn across LLVM majors** — pin one version; golden files pin
   our emission only.
3. **Three semantic twins** (interp, C++, MLIR) — mitigated by the whole-program
   refusal model: MLIR either takes the full corpus test or declines it,
   so there is no partially-covered gray zone.
4. **`cuda-tile` is young and closed to contributions** — 14 commits on main,
   CUDA 13.1+ only; the stock `gpu`/`nvvm` path is the hedge in P3.
5. **Fold-order regressions** — the single most likely source of silent
   divergence; the license split in §4 is load-bearing and needs its own
   corpus category from day one.
