# A06: C++ PM03 same-cell ordinary-return parity — source census/design

Source-only checkpoint: lane `a-lifetimes`, HEAD
`c4637868c58bc36157ef9496da982d867921f09f`. A05 is frozen and still under sole-runner
validation. This note is the only authored file. No build, lowering, emission,
execution, test, benchmark, Git mutation, or new adapter/fixture occurred here.
Graft `rewritePoolAlias` search returned no indexed hits (68 files); F# context
was read directly after that empty result.

## Finding and proposed single card

C++ PM03 parity remains open for the exact A04 ordinary return. Accepted A04
default-scheduler C++ output has two allocations even with reuse enabled. Current
source still supplies only the generic dead-input donor path, whose admission
correctly excludes a donor read by the current consumer. This is a distinct
missing backend realization, not evidence for relaxing generic reuse. A05's
different-extent, two-formal early-release transform does not admit this one-formal
same-cell tail (`EarlyPoolRelease.fs:163–184,227–234`).

Propose one C++-only return realization with an explicit immutable semantic
certificate and a separate actual allocation-frame check. Use the existing scope
tracking and return suppression for ownership. Emit the final map through one
pool pointer, loading the old cell into a scalar before storing that same index.
Do not place this certificate in `PoolReuseTable.ReturnDonors`, alter `deadFrom`,
rewrite an ordinary fresh-output loop into two aliased restricted pointers, or
introduce a lifetime event/API. The current source does not already offer a
safe ready-made same-cell fill consumer; that small dedicated fill is the
implementation prerequisite, subject to root approval. This note implements none
of it and closes no wider PM03 variant.

## Actual evidence and its identity

The best already-captured positive is the original
`tests/corpus/memfree/033_same_cell_return_owned.blade`, SHA256
`613343A9C3F9F9766A3E4BBA85BC3FC9CE39349EC7428C9A876D36796646DBBD`.
`same_cell(x)` allocates `p = compute(x * 2 + 1)`, completes `old_sum = reduce(p,+)`
and `bias = old_sum - 1`, then returns `compute(p * 0.5 + bias)`. Extent is seven.
Two calls remain, followed by reads of both results and prints of both original
inputs, both results, and both sums (all six existing pins remain required).

Saved `bench/.build/A04-033-default-52f4/cpp-reuse{1,0}.emitted.cpp` are identical,
SHA256 `04494AFE08CD8B09B721A64B988E627537083E970F235F941719070B17C8DC51`.
Compiler source checkpoint was `52f4bcc31849def15ee50e0ddce353bf7113cbab`;
`A04-tests.md:19–39` records fresh DLL
`CF599009323132F2805139134CF4333C835C3170B6CA859CC7537C5DBE705B03`
and executable `98DBD84687FB4EA95756C1D7C4AF5BD2036A7CBC8BBF3F6CE74F6B3A3DD47CCD`.
The invocation pins reuse 1/0, fusion on, CSE off, strict reproducible arithmetic,
contract off, BLAS off, serial OMP, PRINT unset; scheduler gates are unset.
These are actual default-scheduler results under that arithmetic policy, not a
new run of current HEAD or evidence for every floating-point build policy.

Exact C++ contract in that artifact (`same_cell`, lines190–223):

| Order | Actual storage/action |
|---|---|
| 195 | Allocate seven doubles for `__v12` (fresh producer backing). |
| 199–204 | Read `x[k]`, write `__v12[k]`; distinct producer/input pointers. |
| 206–207 | Finish the complete old-value reduction and bias scalar. |
| 211 | Allocate separate seven-double `__ret1073741824`. |
| 215–220 | Read `__v12[k]`, store result to the fresh return backing at `k`. |
| 222–223 | Free `__v12` once, return the other backing without freeing it. |

`main` calls at287/289 and performs both result sums/prints afterward. This is
backing/allocation/free evidence, not a variable-name or logical-peak estimate.
Accepted LLVM ON/OFF evidence is already recorded in `A04-review.md:122–159`:
ON returns the sole producer root, OFF allocates a distinct result then frees the
producer. That proves LLVM's narrow route, not C++ parity.

Existing refusal controls also have actual historical C++ evidence under
`bench/.build/A04-runner/paired-02-reuse1/arm.cpp` (caller-owned) and
`paired-03-reuse1/arm.cpp` (retained name/tuple). Their paired provenance identifies
integration source `5e208a77`, DLL
`FE12B5CEC4CAFC311D393160835278EDC93828AFEDC2368F820F5CA0CCE172AD`;
these used isolated scheduler gates and are not current/default-schedule results.
Caller-owned has a fresh result, no local producer/free. Retained-name explicitly
copies `p` to `held` and returns a tuple; it is not proof of physical aliasing.
No new runs are needed to rediscover those historical facts. Existing synthetic
A04 refusal checks (`LlvmTests.fs:1944–1984`) cover captures/type dependencies,
gather, failure, deferred inputs, lifetime events and competing owners, but assert
LLVM decisions; C++ admission will require its own checks if implemented.

## Current source topology and existing mechanisms

* `Optimize.fs:690–705`: `deadFrom` rejects reads at **or after** the consumer.
  `p` is read by the tail, so generic donors stay empty. Preserve this rule and
  all other existing dead-pool consumers, including A03 distinct-input donation.
* `CodeGen.fs:800–829`: ordinary computed return calls `genApplyCombinator` with
  a new return name; only a generic `currentReturnDonor` triggers declaration
  rewriting. It suppresses the returned name's free and frees the rest.
  `genFuncDef` installs/clears the generic donor per function (1543–1557).
* `CodeGenLoopNest.fs:3018–3130`: the flat map constructs distinct restricted
  operand pointers and a restricted output pointer, then assigns the generated
  kernel expression to output[k]. Its IVDEP/OMP explanation assumes a fresh
  output. `rewritePoolAlias` (5018–5033) changes only the allocation declaration;
  it cannot repair that access-path proof when output is also its input.
* `CodeGenExprSupport.fs:636–644` / `CodeGen.fs:272–277`:
  `copyInPlaceAssign` is a module mutable-target `std::copy_n` of an already
  materialized IRVar RHS. It neither removes the RHS allocation nor evaluates a
  pointwise return in the donor. It is not a reusable admission shortcut.
* `CodeGen.fs:1099–1173` unrolls/hoists the actual body, computes escapes, pushes
  `SFunc`, and truncates in `finally`. Tracking records the actual pool name,
  free routine/template, extents ownership and binding owner
  (`CodeGenLoopNest.fs:4771–4842,4904–4927`). `renderFrameFrees`/suppression
  (5120–5149) already express a pool leaving via a return. No early-free or
  release protocol is needed for the proposed parity.
* LLVM's private `sameCellReturnCertificate`/`sameCellReturnBacking`
  (`EmitLlvm.fs:7826–7998`) separate original-body proof from actual one-pool,
  one-tracking-entry realization. They are a reference contract, not a callable
  C++ API. `CodeGen` compiles before `EmitLlvm`; do not reach forward or use a
  mutable cross-backend approval side table.

## Bounded certificate and realization contract

Admission must be attached to the actual original callable/body and final module
context being emitted, before codegen's let hoisting, and rechecked against the
exact realized prefix/tail. Pass an immutable candidate explicitly into only
the ordinary function return path; default `None` for nested arms, main-local
closures, expression/IIFE/kernel-as-value renderers and unsupported contexts.
Use per-call `BLADE_POOL_REUSE` and a distinct truthful C++ decision. Never infer
approval from the emitted function name or from LLVM's prior emission. If a
common semantic helper is extracted later, it needs fsproj placement before both
backends and independent backend realizations; extraction is not required by
this census and must not widen the accepted shape.

Required complete proof for this single kernel:

* Ordinary nonstatic, nonrecursive, capture-free serial direct function, no CUDA,
  MPI, OMP, arity polymorphism, mutation or external/output effects. Parameter
  positions and all declarations are unique; stale/rebound identities refuse.
  Return/formal/producer/tail types are direct unitless F64, rank-one positive
  literal plain dense storage, nonvirtual/no identity; exact `sameIndex`
  provenance, not extent-only equality. No units/wrappers/layout conversion.
* Exactly one first local computed scalar map of an original stored formal,
  followed only by completed F64 scalar observations: at least one positive-
  extent scalar sum, then closed scalar `+`, `-`, `*` values. Preserve the
  reduction's existing order/licence and every original scalar evaluation. No
  speculative forcing or new arithmetic traversal, reassociation, contraction
  setting, allocation or repeated scalar evaluation.
* Final map has one operand, precisely that direct local donor, same index at
  every cell. Kernel is closed total F64 `+`, `-`, `*`, with only the leaf cell
  and already-completed scalar snapshots as inputs. Check callable arrow,
  parameter indexes/types/unique IDs, capture IDs/types and bodies; host and map
  parallel metadata, ApplyInfo and underlying loop arrays/types/identities,
  dimensions, symmetry, Reynolds flags and side metadata must match the narrow
  plain map. Scalar fold commutativity metadata is retained, not erased.
* Reject caller-owned storage, alias/tuple/view/retained/deferred handles, gathers,
  shifted/stencil/scatter reads, conditionals/calls/unknown or failing kernels,
  assignment, lifetime regions, extra allocations and future donor reads.
  Check all final-module roots, other callable bodies/captures and parameter/
  result types, binding types/side operands/source refs, and index extents **and
  Dependencies**, not only body expression refs or printer output. A05's full
  root census is a useful read-only precedent; leave A05 bytes unchanged.
* Reject every competing planner key **and value** intersecting the body:
  generic/current/return donors, hot-row, cell-fold, rec-step, epilogue,
  compaction/compact-row, member and orbit owners. No planner may subsequently
  change the certified body. Detect changed function/body/context, never cache
  authorization across functions or codegen invocations.
* Immediately before tail emission require depth-one current `SFunc` with
  exactly one actual `PoolAlloc`, owner=donor, exact current donor C++ binding,
  expected dense free routine/template, no owned dynamic extents, no escaped or
  suppressed donor, and no other registration/name for that allocation. All
  snapshots must already be emitted scalar bindings. Closed syntax plus the
  full demand/metadata proof rules out invisible aliases; tracking names alone
  are insufficient. Missing/ambiguous realization declines before any tail
  allocation, free or speculative evaluation.

Successful fill emits one writable pointer based on `p` and no independent
restricted input/output paths. In each iteration: `old = pool[k]`, evaluate the
unchanged scalar kernel with its leaf mapped to `old`, then `pool[k] = value`.
The explicit old scalar establishes read-before-write. Different iterations
touch distinct cells and only read completed invariant scalars, so the dedicated
loop may retain IVDEP with this new proof. Do not globally disable optimization
to manufacture a route, reuse the fresh-output rationale, or emit an OMP promise
for this serial-only card. One restricted pointer is legal only when all donor
accesses in that block derive from it; using an unqualified single pointer is
also semantically sufficient. `blade_portability.hpp:44–84` selects restrict/
IVDEP spellings and should not change.

Only after successful emission suppress that exact donor record and return its
original wrapper (whose constexpr extents survive). No result allocation,
second owner, in-callee producer free or hidden extra wrapper/free. The existing
fresh-return classification stays correct because the returned pool was freshly
allocated from the caller's input, not borrowed from it. Failure of either
certificate or realization falls back to the original fresh-result path once,
with unchanged diagnostics/failure timing. No allocator retention, RSS or speed
claim follows from removing one allocation/free pair.

## Deferred sole-runner emission contract

Root accepts the existing two-root033 evidence as sufficient for this deferred
planning card. **No A06 execution is queued or requested in this final round.**
If separately reopened, the entire initial diagnostic is at most two C++
emissions of unchanged033, reuse1/0, with an already-fresh root-approved DLL,
actual default fusion/scheduler, saved strict FP policy, PRINT unset, caches off,
`BLADE_LLVM=off`, and A05 early-release=1 in both arms. Save compiler/input
identities, complete validated IR/capture/type metadata, decisions and donor
tables before/after emission, settled refusals and raw C++. Stop for review on
changed shape or unexpected ownership; no native execution, LLVM emission,
control matrix or rebuild belongs to that initial diagnostic. The existing
historical caller/tuple evidence remains explicitly historical.

After separate implementation authorization, C++ tests must independently prove
the new one-root read-before-store/return graph, both-calls/all-six-output pins,
fallback/refusal decisions and unchanged generic tables, plus affected category
and instrumented payload checks. Existing LLVM test passes do not supply those
C++ guarantees. No such validation is claimed by this note.

## SHA256 context (working bytes, not normalized Git blobs)

| Path | SHA256 |
|---|---|
| `src/CodeGen.fs` | `531D38D9BD44101C67EDB8D00555A8397A15CA189B1B8EA39D89F08B5E98C4AA` |
| `src/CodeGenLoopNest.fs` | `CE9359328D4C3207EAE24C27F2B77BE9698B76C87A35159AC7B00F36186B3E03` |
| `src/CodeGenExprSupport.fs` | `99AE0872F95E31CC8EE64318860257544AFBAFF389172F03E766E9FE940D3480` |
| `src/CodeGenState.fs` | `E836AEF9B781FC6309264D7A4AD27F38B4D13B5BED3A2507AAB4E494DC187E2E` |
| `src/Optimize.fs` | `3B72EE3E74BA9FFC692B0C11D434579817FCD82599147026983EA904CBB0DEEC` |
| `src/EmitLlvm.fs` | `B8DBF9D8BC6E21B8B6BBCE0208635AE70652962ECFFF316BB41516FB6C75CD18` |
| `src/EarlyPoolRelease.fs` | `2F11BD292AF4BF4962AFDA87F1E3596F6072F42C719B3DB6041D048EF55FEF0D` |
| `src/cpp/blade_portability.hpp` | `B831ADDE94B841D9BD548810E137A2E79377CDDAEA608A4D3A0462C52F0C98CE` |
| `src/IR.fs` | `90FCF321A3A019F2EE75C519DCA44B9C1127881271D509FB054212DB0EBD6216` |
| `src/Types.fs` | `115118D1315A6BC8A85DC4057563A8D1671E347E58C925E38FD9C7647694DAD8` |
| `src/Lowering.fs` | `2A879AB4BCAE71777C989555195F5710C129ADB6A5EB2817BDA42C9FDCA65451` |
| `tests/LlvmTests.fs` | `77A9D9032EF71D4C07C4445B6B0A77C75BE843C283F24DECF92D1733F1621AF0` |
| `Blade.fsproj` | `0CACDC780D1BB82950CC6F84A19DAFDD46CE4F8E5A7587571E6682FDC6360DC9` |
| `bench/notes/round4/A04-tests.md` | `B348F25619E3B1181C8035ACAC132FC0C4E7F186DBC89AF708330B65835C003B` |
| `bench/notes/round4/A04-review.md` | `085B524AF689B8907528C8C4F6A5CE0B0A92549A214F55F2150AED6D783C1A1E` |
| `bench/.build/A04-033-default-52f4/cpp-reuse1.invocation.txt` | `65142A85013D9563941AEF542C81749F8F403C92243D3A02C76E7CF36DB13C31` |
| `bench/notes/round4/A04-census-inputs/02_caller_owned.blade` | `A282AF7E2182CA23B050690F809BAD46533B3D0FFF46812F8631C8279A0C59B6` |
| `bench/notes/round4/A04-census-inputs/03_retained_name.blade` | `FC8AB484999CDCF6D38E08548B891247F92607C7F5FE6E7DBE36C94898EA446F` |
| `bench/.build/A04-runner/paired-02-reuse1/arm.cpp` | `4144B90FA8084C89E44B36F47FFF0F488C91BDC630BE21F2923FC0D4F23C65B4` |
| `bench/.build/A04-runner/paired-03-reuse1/arm.cpp` | `2B9AD0B15B5E4DBB9503B3C9712F9BA4302AD53CF3E5A1CF088A1AAE00C903FD` |

Root review decision pending. A05 remains untouched; PM03 general aliasing,
runtime shapes, tuple/let consumers, parallel kernels and all wider variants
remain open.
