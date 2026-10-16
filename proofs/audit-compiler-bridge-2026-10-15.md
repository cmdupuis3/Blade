# Proof audit, compiler bridge — 2026-10-15

The proof audit (`audit-2026-10-15.md`) found prose that claims more than
the theorems prove. This follow-up asks the next question: does the
COMPILER rely on any of those overclaims? Probed against master `60f51458`
(`bin/Release/net10.0/Blade.exe`), both lanes. Every result below was
reproduced twice: once by the probing agent, once independently from the
same sources. Probe sources are inline.

## Status (2026-10-15): fixed, in the commit "Proof-audit compiler fixes: struct lower bound, symmetry checks, sparse keys"

- **B1** fixed (`src/StructIdxFence.fs`, tests index-types/487-489).
- **B2, B3** and the side finding fixed: the witness evaluates integer
  kernels with §2.4 semantics, checks f(a, a) = 0 on the diagonal, and
  samples captured Int/Float scalars as free inputs (tests symmetry/080-088).
- **B4** documented as a licence in `docs/formalism.md` (symmetry deduction
  is exact-ring algebra; a declared comm on a float kernel serves permuted
  reads from the canonical evaluation), pinned by symmetry/086.
- Found while fixing: a key outside a sparse key set died BL8005
  "unordered_map::at" in the compiled lane and BL8003 in the interpreter;
  both now abort BL8006 (index-types/490). And the interp / diff-oracle
  differentials shared a fixed %TEMP% directory, so two concurrent runs
  rewrote each other's headers and the exe cache kept the stale result;
  the directory is now per process.
- Full default suite with those fixes: TOTAL 6391 passed, 0 failed.

## Confirmed: wrong output

### B1 — a single-field lower bound in a constrained struct is dropped

`src/StructIdxFence.fs:325`. A one-variable form `a·x_k + K <= 0` with
`a = -1` means `-x_k <= -K`, so the bound is `tighten -1 k (-f.K)`. The code
passes `f.K`, which stores a bound looser than the box, so it is ignored.

```blade
static struct Big { i: Int<min=0, max=200000> } where i > 199998   // 2 solutions
let R = method_for(range<Big>) <@> lambda(i) -> 1 |> compute
let n = extents(R)
let s = reduce(R, (+))
let V = method_for(range<Big>) <@> lambda(i) -> i |> compute
let first = V(0)
```

Observed `n = 200001`, `s = 200001`, `first = 0`; expected 2, 2, 199999.
Below `maxBoxCells = 100_000` (`src/StructIdxSpec.fs:101`) the counting
certificate (`StructIdxSpec.fs:522`) catches it as an internal error
(BL9001 "closed-form enumeration (3 cells) disagrees with the counting
layer"; `where i > 5`, `i >= 3`, `i > 1` all die this way). Above the cap the
plan is not certified, and the wrong enumeration runs SILENTLY. Upper bounds
and difference constraints are correct. No corpus test uses a single-field
lower bound. Not related to any proof; found while probing F05.

### B2 — `anticomm` storage drops a nonzero diagonal in wrapping Int64 (F07)

The audit's F07: anti-invariance forces a zero diagonal only on a carrier
without 2-torsion. Blade's Int64 wraps (formalism §2.4), and
x = −x also holds at x = Int64.MinValue.

```blade
let c = -9223372036854775807 - 1          // Int64.MinValue, captured
let A = [1, 2, 3]
let compact = method_for(A, A) <@> lambda(x, y) where anticomm(x, y) -> (x - y) + c |> compute
let dense   = method_for(A, A) <@> lambda(x, y) -> (x - y) + c |> compute
let compact_diag = compact(1, 1)
let dense_diag = dense(1, 1)
let law_holds = dense(0, 2) == -dense(2, 0)
let diag_law = dense(1, 1) == -dense(1, 1)
let rows_compact = reduce(decompact(compact, 0), (+))
let rows_dense = reduce(dense, (+))
```

Observed: `law_holds = true` and `diag_law = true` (the licence is honest
under wrap), yet `compact_diag = 0` against `dense_diag =
-9223372036854775808`, and the row sums differ in every row. The claim is
trusted because the witness evaluator gives up on a captured variable
(`src/Deduce.fs:958`); `src/IRLoopStructure.fs:656-658` then picks strict
`AntisymIdx` storage, whose diagonal read is an implicit 0
(`src/CodeGenExpr.fs:624-625`, `src/Interp/ArrayOps.fs:568`). The promise is
`docs/formalism.md:504-506` and :1890.

Float twin: `(x - y) / 0.0` (sign law deduced) gives `compact_diag = 0.0`
against `dense_diag = nan`.

### B3 — the anticomm witness never samples the diagonal

`src/Deduce.fs:852` says the witness is "complete for any body the
evaluator can run", but its samples never include a = b. A claim false
ONLY on the diagonal is accepted:

```blade
let A = [1.0, 2.0, 3.0]
let compact = method_for(A, A) <@> lambda(x, y) where anticomm(x, y) -> if x > y then 1.0 else -1.0 |> compute
let dense = method_for(A, A) <@> lambda(x, y) -> if x > y then 1.0 else -1.0 |> compute
```

`compact(1, 1) = 0.0`, `dense(1, 1) = -1.0`; row sums `[-2, 0, 2]` against
`[-3, -1, 1]`. The user's claim is false here, but the docstring promises
that the witness refutes it.

### B4 — `comm` deduced by associativity is not bitwise in floats (S01's Blade shape)

```blade
let A = [1.0, 1.0e16, -1.0e16]
let compact = method_for(A, A, A) <@> lambda(x, y, z) where comm(x, y, z) -> x + y + z |> compute
let dense   = method_for(A, A, A) <@> lambda(x, y, z) -> x + y + z |> compute
```

`compact(1, 2, 0) = 0.0`, `dense(1, 2, 0) = 1.0`. The parity tables
(`src/Deduce.fs:55-80`) prove `comm` of `(x + y) + z` by exact
associativity; compact storage computes one ordering and serves every
permutation from it. `docs/formalism.md` never qualifies symmetry deduction
by rounding. Whether this is a bug or a licence is a design decision
("the fastest way is the only way" vs bitwise agreement with the dense
meaning); today it is undocumented.

## Side finding

The witness evaluator runs Int64 kernels in `float` (`src/Deduce.fs:885-932`;
`isInt` only switches `/` and `%` truncation). Written with a LITERAL
MinValue, B2's honest claim is REFUSED (BL4013, "at x = 2, y = 1 the body is
-9.22337E+18 ... not 9.22337E+18"): conservative, but the evaluator does not
implement §2.4 and prints float values for an integer kernel.

## Not reachable

- **F02 (Jacobian).** Forward mode's symmetric path
  (`src/GradSweeps.fs:1054-1111`) uses the joint-swap theorem and agrees
  with the dense and hand-derived tangents. Reverse mode refuses `where`
  map kernels (`src/GradNormalize.fs:279`), and a `SymIdx` parameter with an
  asymmetric cotangent accumulates W(i,j) + W(j,i), exactly T3.
- **F05 (rank-zero compound).** Fenced: `mask(3, …)` BL3007,
  `compound(3, m)` BL3999, empty struct BL4020; an all-false mask has
  extent 0 and `reduce` refuses it (BL8003).
- **F01 (six-component access).** No such code in `src/`.
- **S02 (sentinel minimum).** No sentinel-seeded minimum in `src/`.
- **R01, R02.** Model-only.

The MCP `blade_check` / `blade_eval` tools report B1's internal error as a
protocol error ("rebuild the compiler") instead of a diagnostic.
