# Orbit work, measured: Blade against its prediction, NumPy and BLAS `dsyrk` measured, SySTeC specified

**Status 2026-10-12: MEASURED, NOT A THEOREM.** Item 8 of
`docs/plans/plan-one-model-surface-calculus.md`. The theory predicts the work
(kernel evaluations) and storage (cells) of a symmetric computation as the orbit
count of its licence group. This page records:

- the prediction for each row;
- Blade's measured counts at four extents;
- NumPy's measured counts;
- one published hand-optimized symmetric kernel in another framework, measured:
  BLAS `dsyrk` through SciPy 1.18.0 / scipy-openblas 0.3.31.dev;
- the SySTeC experiment, **specified, not run**: there is no Julia on this
  machine.

Five of six Blade row families match the prediction at every extent measured. The
sixth, `(A, B, A)` with `comm`, does not. The compiler takes a larger quotient than
the surface calculus licenses. Its output-type deduction moves B's axis to the
end, and in source coordinates the returned array breaks the symmetry the user
declared (section 4).

## 1. The prediction

Notation: `A : Array<Float64 like Idx<n>>`, `B` over `Idx<m>`, and the kernel is a
counted user function. "Work" is the number of kernel evaluations; "cells" is the
number of stored result cells. The theory predicts that both are the same number,
one per orbit of the licence group. The dense (collection-first) count is `n^r`.

| Row | Program | Licence group | Predicted work = cells | Dense | Source |
|---|---|---|---|---|---|
| R1 | `method_for(A, A) <@> f`, `f` with `where comm(x, y)` | S_2 on one identity group | C(n+1, 2) | n^2 | `covariance_shape`, `typed_app_is_quotient` (BladeSurface) |
| R2 | `object_for(k) <@> (A, A, A)`, `k` a `Poly<T^0>` kernel with `where comm(a)`; and `method_for(A, A, A)` with a `comm(x, y, z)` lambda | S_3 | C(n+2, 3) | n^3 | same; formalism 8.4 |
| R3 | `method_for(A, A, B, B)`, `where comm(x, y), comm(z, w)` | S_2 x S_2 (distinct groups multiply) | C(n+1, 2) * C(m+1, 2) | n^2 m^2 | formalism 8.4 step 4 |
| R4 | `method_for(A, B, A)` with `comm(x, y, z)` or `comm(x, z)` | trivial: three singleton identity groups (formalism 8.3) | n^3 | n^3 | `aba_unlicensed` (BladeSurface) |
| R5 | `method_for(A, A)`, kernel WITHOUT `where comm` | trivial | n^2 | n^2 | `comm_license_needed` (BladeSurface) |
| R6 | `method_for(A, B)`, `comm(x, y)`, two arrays of equal extent | trivial (Stab is trivial) | n^2 | n^2 | formalism 8.3 |
| R4c | `method_for(A, B, A)`, no `comm` (control) | trivial | n^3 | n^3 | `comm_license_needed` |

R4, R4c, R5 and R6 are the negatives: a quotient appearing in any of them would
falsify the corresponding statement.

For R4 the tower's documents give a second number. `plan-uniform-optimality.md`
defines the licence group as G = H ∩ Stab, where Stab is "the permutations
preserving identity classes" and the classes are by array name, not adjacency.
Under that definition, `(A, B, A)` with `comm(x, y, z)` or `comm(x, z)` has
G = {id, (1 3)} and C(n+1, 2) * n free orbits. That plan's output `Out(ix)` keeps
the source coordinates. The surface calculus (`out_shape`, built on the run-length
grouping `rle`) and formalism 8.3 give n^3 instead, which is also the item's stated
prediction. The two statements disagree on this row, and both numbers are
reported below.

The numbers at the extents measured (none a power of two, per the benchmark
discipline):

| n | C(n+1,2) | n^2 | C(n+2,3) | n^3 | C(n+1,2)·n |
|---|---|---|---|---|---|
| 7 | 28 | 49 | 84 | 343 | 196 |
| 11 | 66 | 121 | 286 | 1331 | 726 |
| 23 | 276 | 529 | 2300 | 12167 | 6348 |
| 45 | 1035 | 2025 | 16215 | 91125 | 46575 |

## 2. Method

**Compiler.** `Blade.exe` v0.20.0 from the sibling checkout `GitHub\Blade`
(`bin\Release\net10.0`, built 2026-10-11 21:51), used read-only. The checkout was
on branch `fix/kernel-helper-module-capture` at `7440a74b` with an uncommitted
`src/CodeGen.fs` edit, so the build may include that edit. g++ 16.2.0 (MSYS2
ucrt64). `BLADE_OMP_THREADS=1`: no parallel body is emitted. The kernels carry no
`omp` licence anyway, and a run without the variable emits no pragma.

**Three tools produced the numbers.** Each number below names its tool.

| Tool | What it gives |
|---|---|
| CLI `blade run` / `blade plan` / `blade emit` (PowerShell) | codegen lane: compiled C++ run by g++ |
| `blade_check`, the Blade MCP server's check tool (tier full) | binding types and deduced facts; the CLI `blade check` prints only `OK` |
| `blade_eval`, the Blade MCP server's REPL | its reply's `lane` field says which lane ran: `interp` or the `gpp` fallback |

**Counting work.** A captured counter does it. A serial kernel may assign a
captured `let mut` binding (CLAUDE.md, "Captured writes race only in parallel
bodies"). No counter program was refused, so no fallback was needed. The counted
R1 program:

```blade
let A = 1.0 + Float64(0..7)
let mut calls = 0
let f = lambda(x, y) where comm(x, y) -> {
    calls = calls + 1
    x * y
}
let result = method_for(A, A) <@> f |> compute
let n_calls = calls
// EXPECT: n_calls = 28
```

The other rows replace the kernel and the call (`B = 2.0 + Float64(0..m)`):

```blade sketch
// R2a  function f(a: Poly<T^0>) where comm(a) -> T^0 = { calls = calls + 1; a[0] * a[1] * a[2] }
//      object_for(f) <@> (A, A, A)
// R2b  lambda(x, y, z) where comm(x, y, z) -> { ...; x * y * z }      method_for(A, A, A)
// R3   lambda(x, y, z, w) where comm(x, y), comm(z, w) -> { ...; x * y * z * w }   method_for(A, A, B, B)
// R4a  lambda(x, y, z) where comm(x, y, z) -> { ...; x * y * z }      method_for(A, B, A)
// R4b  lambda(x, y, z) where comm(x, z) -> { ...; x * y + z * y }     method_for(A, B, A)
// R4c  lambda(x, y, z) -> { ...; x * y * z }                          method_for(A, B, A)
// R5   lambda(x, y) -> { ...; x * y }                                 method_for(A, A)
// R6   lambda(x, y) where comm(x, y) -> { ...; x * y }                method_for(A, B)
```

**Counting cells.** Cells are counted twice: in the counted program, and in a
counter-free twin (expression-bodied kernel, no `let mut`). The count is the number
of values in the printed `result`, which the CLI `blade run` prints by walking the
stored layout: rank-2 symmetric results print as ragged rows, packed rank-3 and
rank-4 results print flat. To confirm that the print is the storage, I checked
`blade emit` for R1 at n = 7. The result is allocated with
`result_symm[2] = {1, 1}`, and each row is addressed at
`__ooff_result_0 = (28) - ...` into one pool of 28 doubles.

**Plans and diagnostics.** CLI `blade plan` was run on both twins, and the plans
were identical on every row: the counter changes no decision. CLI `blade check` was
run on every counter-free twin and its diagnostics recorded.

**Interpreter lane.** `blade_eval` reply `lane: "interp"`, value 28, gives the same
count for R1 at n = 7.

**Scratch artifacts** (not in the repository), under `%TEMP%\claude\item8\`:

- `measure.ps1`: generator and runner.
- `results.tsv`: the measured table.
- `runs\`: the 72 measurement programs and their outputs, plus the section-4
  evidence:
  - `ev_*.blade` with `ev_*.run.out` (CLI runs) and `ev_mixed_xz_noread.emit.cpp`;
  - `mcp_blade_check_types.txt` (raw `blade_check` replies);
  - `mcp_blade_eval_interp_evidence.txt` (raw `blade_eval` replies, each with its
    `lane`).
- `numpy_orbit.py`, `numpy_results.tsv` and `numpy_results_EP.tsv`.
- `scipy_syrk.py` and `scipy_syrk_results.tsv`.

## 3. Blade, measured

All counts are from CLI `blade run`. Calls = the counter after the run. Cells (c) =
printed cells of the counted program. Cells (p) = printed cells of the counter-free
twin.

| Row | n | m | Predicted | Calls | Cells (c) | Cells (p) | Dense | Match |
|---|---|---|---|---|---|---|---|---|
| R1 | 7 | | 28 | 28 | 28 | 28 | 49 | yes |
| R1 | 11 | | 66 | 66 | 66 | 66 | 121 | yes |
| R1 | 23 | | 276 | 276 | 276 | 276 | 529 | yes |
| R1 | 45 | | 1035 | 1035 | 1035 | 1035 | 2025 | yes |
| R2a (Poly) | 7 | | 84 | 84 | 84 | 84 | 343 | yes |
| R2a (Poly) | 11 | | 286 | 286 | 286 | 286 | 1331 | yes |
| R2a (Poly) | 23 | | 2300 | 2300 | 2300 | 2300 | 12167 | yes |
| R2a (Poly) | 45 | | 16215 | 16215 | 16215 | 16215 | 91125 | yes |
| R2b (lambda) | 7 / 11 / 23 / 45 | | as R2a | 84 / 286 / 2300 / 16215 | same | same | as R2a | yes |
| R3 | 7 | 11 | 1848 | 1848 | 1848 | 1848 | 5929 | yes |
| R3 | 11 | 7 | 1848 | 1848 | 1848 | 1848 | 5929 | yes |
| R3 | 23 | 11 | 18216 | 18216 | 18216 | 18216 | 64009 | yes |
| R3 | 45 | 23 | 285660 | 285660 | 285660 | 285660 | 1071225 | yes |
| R4a `comm(x,y,z)` | 7 | | 343 | **196** | 196 | 196 | 343 | **NO** |
| R4a | 11 | | 1331 | **726** | 726 | 726 | 1331 | **NO** |
| R4a | 23 | | 12167 | **6348** | 6348 | 6348 | 12167 | **NO** |
| R4a | 45 | | 91125 | **46575** | 46575 | 46575 | 91125 | **NO** |
| R4b `comm(x,z)` | 7 / 11 / 23 / 45 | | n^3 | **196 / 726 / 6348 / 46575** | same | same | n^3 | **NO** |
| R4c no comm | 7 / 11 / 23 / 45 | | n^3 | 343 / 1331 / 12167 / 91125 | same | same | n^3 | yes |
| R5 no comm | 7 / 11 / 23 / 45 | | n^2 | 49 / 121 / 529 / 2025 | same | same | n^2 | yes |
| R6 `(A, B)` | 7 / 11 / 23 / 45 | | n^2 | 49 / 121 / 529 / 2025 | same | same | n^2 | yes |

What CLI `blade plan` reports (identical at every extent):

| Row | `symmetric-storage v1` | `triangular-iteration v1` |
|---|---|---|
| R1 | applied: packed SymSymmetric storage over 2 levels (one canonical cell per orbit); operands: A, A | applied: triangular levels 1; speedup x2 |
| R2a, R2b | applied: over 3 levels; operands: A, A, A | applied: triangular levels 1, 2; speedup x6 |
| R3 | applied: two packed pairs; operands: A, A, B, B | applied: triangular levels 1, 3; speedup x4 |
| R4a, R4b | **applied: packed SymSymmetric storage over 2 levels; operands: A, B, A** | **applied: triangular levels 1; symcom states: SCNeither, SCCommutative, SCNeither; speedup x2** |
| R4c, R5 | no line (only the elementwise-fusion decision is listed) | no line |
| R6 | declined: the commuting positions hold different arrays (A, B); packed storage is licensed only when the SAME array fills them | declined: every level iterates the full box |

**Diagnostics** (CLI `blade check` and `blade run`). R5 carries the expected
warning; no other row has a diagnostic:

> `warning[BL4010]: kernel deduces commutative in (x, y) and both positions receive the same array: output storage is DENSE today. Pin `where comm(x, y)` on the kernel to opt into compact symmetric (triangular) storage.`

For R4c's kernel `x * y * z`, `blade_check` reports one deduced fact, `comm(x, y)`,
an adjacent pair. Those two positions hold different arrays (A, B), so the absence
of BL4010 is consistent with the rule. In this program, deduction does not name
the non-adjacent (x, z) pair, which is the pair the explicit-`comm` R4 rows
quotient.

Ratios dense / measured on the matching rows approach the group order as n grows:

| Row | n = 7 | n = 11 | n = 23 | n = 45 | Limit |
|---|---|---|---|---|---|
| R1 | 1.75 | 1.83 | 1.92 | 1.96 | 2 |
| R2 | 4.08 | 4.65 | 5.29 | 5.62 | 6 |
| R3 | 3.21 | 3.21 | 3.51 | 3.75 | 4 |

The plan's "speedup x2/x6/x4" is the asymptotic ratio, not the ratio at the
extent measured.

## 4. Comparison

- **R1, R2 (both spellings), R3: match at every extent.** Work = cells = the
  predicted orbit count, and `blade plan` names the quotient. Blade attains the
  bound on these rows. The stored layout is the compact one (one cell per orbit),
  not a triangle copied into a full array.
- **R5, R6, R4c: match (the negatives hold).** There is no quotient without
  `comm`, and none when the commuting positions hold different arrays.
  `comm_license_needed` survives the measurement.
- **R4a, R4b: MISMATCH against the item's prediction (n^3), against formalism 8.3
  ("`(A, B, A)` is three singleton groups"), and against `aba_unlicensed`.**
  Measured work and storage are C(n+1, 2) * n at every extent. The measurement
  falsifies the correspondence between the compiler and the surface calculus on
  this row. It agrees with `plan-uniform-optimality.md`'s definition of the
  licence group (H ∩ Stab, Stab by name) **in count only**: that plan's `Out`
  keeps the source coordinates, and the array the compiler returns is not that
  `Out`. Details below.

**Where the defect sits: output-type deduction.** Grouping by array name
concatenates the group axes, so B's axis moves to the end. `blade_check` types the
R4 result as `Array<Float64 like SymIdx<2, 4>, Idx<4>>` when A and B are both
built over `0..4`, for both `comm(x, y, z)` and `comm(x, z)`. With A over `0..4`
and B over `0..6` it is `Array<Float64 like SymIdx<2, 4>, Idx<6>>`. In both cases
the two A axes come first and B last, not the source order (A, B, A) that
formalism 8.4 step 4 prescribes ("concatenate group contributions in order").
A and B here are built from anonymous ranges; no named index type is declared, and
`blade_check` prints them as `Idx<4>` / `Idx<6>`.

CLI `blade emit` of the `comm(x, z)` program (R4b) with the 0..4 / 0..6 extents
(`runs\ev_mixed_xz_noread.blade`) allocates `result_extents[3] = { 4, 4, 6 }` and
`result_symm[3] = {1, 1, 2}`. Subscripts are consistent with this reordered type:
`result(0, 3, 5)` = 75.0, which is the source's cell (0, 5, 3). That value comes
from both `blade_eval` (lane `interp`) and CLI `blade run`.

**The returned array breaks the declared symmetry, in both lanes.** `comm(x, z)`
declares f(A_i, B_j, A_k) = f(A_k, B_j, A_i). So in source coordinates,
`result(i, j, k)` must equal `result(k, j, i)`. The table reads two such cells,
with A = [1, 2, 3, 4] and B = [10, 11, 12, 13]:

| Program | Read | Positional value (the no-`comm` twin) | Codegen (CLI `blade run`) | Interpreter (`blade_eval`, lane `interp`) |
|---|---|---|---|---|
| `comm(x, z)`, `x * y + z * y` | `result(0, 1, 2)` | 44.0 (CLI and interp) | **36.0** | **36.0** |
| | `result(2, 1, 0)` | 44.0 (CLI and interp) | **50.0** | **50.0** |
| `comm(x, y, z)`, `x * y * z` | `result(0, 1, 2)` | 33.0 (CLI) | **24.0** | **24.0** |
| | `result(2, 1, 0)` | 33.0 (CLI) | **60.0** | **60.0** |
| | `result(0, 2, 1)` | 24.0 (CLI) | 33.0 | not evaluated |

In each pair, the declaration says the two cells are equal, and the returned values
are not. 36.0 = (A0 + A1)·B2, i.e. the compiler's cell (A0, A1, B2). This makes R4
a defect, not an axis-order convention: a program that relies on the symmetry it
declared, in the axis order `method_for(A, B, A)` gives it, reads unequal values.

**With B's extent different from A's, the source-order read crashes.** With A over
`0..4`, B over `0..6` and `comm(x, z)`, take `result(0, 5, 3)`:

- The no-`comm` twin prints 75.0 (CLI `blade run`).
- The `comm` program fails under CLI `blade run` with `program crashed:
  STATUS_ACCESS_VIOLATION (0xC0000005)`, exit -1073741819.

Against the reordered type, this read indexes the `SymIdx<2, 4>` pair with 5: an
out-of-range subscript. The axis is anonymous, and formalism 3.10 leaves subscripts
into anonymous arrays unchecked, so no BL8006 fires.

**What a differential test would see.**

- Equal extents: the interpreter agrees with codegen on every read above, so
  `test interp` would not catch the symmetry break.
- Mixed extents: `result(0, 5, 3)` does NOT run in the interpreter. `blade_eval`
  replied with `lane: "gpp"`, i.e. it fell back to the g++ lane, and that lane
  crashed with exit -1073741819. Meanwhile `result(0, 3, 5)` on the same array ran
  in the `interp` lane. So a differential harness would register a lane failure in
  the mixed case. The reason the interpreter declined the input was not determined.

**No row was refused**, and no counter program drew a BLxxxx diagnostic.

```blade sketch
// sketch: measured defect (section 4), kept unchecked until the regrouping rule is decided
let A = 1.0 + Float64(0..4)
let B = 10.0 + Float64(0..6)
let f = lambda(x, y, z) where comm(x, z) -> x * y + z * y
let result = method_for(A, B, A) <@> f |> compute     // typed SymIdx<2, 4>, Idx<6>
let r = result(0, 5, 3)                                 // no-comm twin: 75.0; this: access violation
```

## 5. NumPy, measured

The brief for this item said no Python was installed. That is wrong. Two Pythons
are present:

| Interpreter | NumPy | Notes |
|---|---|---|
| `C:\Python314\python.exe` (3.14.7) | 2.4.6, in the user site-packages | also has SciPy 1.18.0 (section 6); used for the measurement |
| Anaconda 3.8.8 | 1.22.4 | not used |

Julia is absent (section 7).

**Invocation.**

- `C:\Python314\python.exe numpy_orbit.py` from the scratch directory (no flags).
- A rerun with `-E -P` produced identical output (`numpy_results_EP.tsv`).
- `-I` does not work: it excludes the user site-packages, and fails with
  `ModuleNotFoundError: No module named 'numpy'`.

**What is counted.** The kernel is an opaque counted Python function lifted with
`np.frompyfunc` (one call per output element). NumPy therefore cannot see that the
kernel is commutative, which makes this a uniform program in the sense of the
bound. `einsum` is measured on object arrays of a counting scalar (every binary
`*` counted) and on float64 for the output size.

| Row | Variant | n=7 | n=11 | n=23 | n=45 | Cells |
|---|---|---|---|---|---|---|
| R1 | `u2.outer(a, a)` | 49 | 121 | 529 | 2025 | n^2 |
| R1 | `einsum('i,j->ij', a, a)`, object, `*` counted | 49 | 121 | 529 | 2025 | n^2 |
| R1 | hand-written triangle: `i, j = np.triu_indices(n); u2(a[i], a[j])` | 28 | 66 | 276 | 1035 | C(n+1,2), flat, untyped |
| R2 | `u3(a[:,None,None], a[None,:,None], a[None,None,:])` | 343 | 1331 | 12167 | 91125 | n^3 |
| R2 | `einsum('i,j,k->ijk', a, a, a)`, object | 686 | 2662 | 24334 | 182250 `*` (= 2 per cell) | n^3 |
| R2 | hand-written simplex via `itertools.combinations_with_replacement` | 84 | 286 | 2300 | 16215 | C(n+2,3), flat |
| R3 | 4-way broadcast, (n, m) = (7,11), (11,7), (23,11), (45,23) | 5929 | 5929 | 64009 | 1071225 | n^2 m^2 |
| R4 | 3-way broadcast over (A, B, A) | 343 | 1331 | 12167 | 91125 | n^3 |
| R5 | non-commutative `x - 2y`, `outer(a, a)` | 49 | 121 | 529 | 2025 | n^2 |
| R6 | `outer(a, b)` | 49 | 121 | 529 | 2025 | n^2 |

**Result.** NumPy does dense work and stores dense cells on every row (`n^r`), as
the collection-first prediction says. It reaches the orbit count only when the user
writes the index enumeration by hand (`triu_indices`,
`combinations_with_replacement`). The result is then a flat vector with no
symmetric type: reading cell (i, j) needs hand-written offset math, and nothing
checks that the kernel was commutative. The hand-written rows hit the bound
exactly, which is the attainment half, and no NumPy variant went below it.

A measured program that made fewer calls than the orbit count and was still correct
for every commutative kernel would not falsify T1 (`orbit_lower_bound`). T1 is a
theorem about the oracle model. Such a program would show that it falls outside
that model: it did not treat the kernel as an opaque oracle.

## 6. BLAS `dsyrk` through SciPy, measured

This row is item 8's actual target: a published hand-optimized symmetric kernel in
another framework. `scipy.linalg.blas.dsyrk(alpha, a)` computes C := alpha·A·Aᵀ
for an n × k matrix A and writes ONE triangle of an n × n C. With k = 1 it is R1's
outer product of a with itself.

Environment: SciPy 1.18.0, BLAS = scipy-openblas 0.3.31.dev (from
`scipy.show_config`). Invocation: `C:\Python314\python.exe -E -P scipy_syrk.py`.

**What is measured.** A sentinel-filled C (every cell −7.0) is passed with
`beta = 0`. Cells written = cells that no longer hold the sentinel. The written
triangle was compared against `a @ a.T` and was correct in every case.

**What is not measured.** BLAS works on float64 only, so the multiply-add count
cannot be instrumented. The documented syrk operation count, n(n+1)/2 · k, is shown
beside the measurement and labelled as documented. Blocked implementations may
compute diagonal blocks densely, so the true count can exceed it.

| n | k | Cells written (`lower=0` and `lower=1`) | Returned array cells | Documented multiply-adds n(n+1)/2·k | Dense n²·k |
|---|---|---|---|---|---|
| 7 | 1 | 28 | 49 | 28 | 49 |
| 11 | 1 | 66 | 121 | 66 | 121 |
| 23 | 1 | 276 | 529 | 276 | 529 |
| 45 | 1 | 1035 | 2025 | 1035 | 2025 |
| 7 / 11 / 23 / 45 | 5 | 28 / 66 / 276 / 1035 | n² | 140 / 330 / 1380 / 5175 | 245 / 605 / 2645 / 10125 |

**Result: `dsyrk` attains the orbit count in cells written** (C(n+1, 2) at every
extent, for both triangles). By its documented operation count it also attains it
in work. Its storage is not compact: it returns an n × n array with the other
triangle untouched. The symmetry is a calling convention (`lower`), not a type,
and the caller must know which half is valid. It is also a fixed kernel (the
product), not a user kernel under a declared law. That is exactly the class Blade
generalizes.

## 7. SySTeC, specified (not run: no Julia on this machine)

SySTeC (Patel, Ahrens, Amarasinghe, CGO 2025, arXiv:2406.09266) is the nearest
system. It takes one Finch assignment plus the declared symmetric inputs.
`plan-paper-1-comoment.md` §8.2 summarizes what its text says:

- it exploits a repeated operand in its SSYRK evaluation without stating the rule;
- it computes the canonical triangle and then copies it to a full dense output.

To run the test, install Julia, Finch.jl and the paper's artifact. Invoke each
kernel below through the artifact's entry point, as in the paper's §5.2.4 SSYRK
experiment, with no input declared symmetric. The kernels in Finch's `@finch`
notation (the exact SySTeC driver call is unverified here):

```julia
# a = 1.0 .+ (0:n-1), b = 2.0 .+ (0:m-1); A_mat an n x p matrix; outputs zero-initialised Dense tensors
S1: for j=_, i=_;           C[i, j]       += a[i] * a[j]                 end   # R1
S1': for k=_, j=_, i=_;     C[i, j]       += A_mat[i, k] * A_mat[j, k]   end   # SSYRK, p columns
S2: for k=_, j=_, i=_;      C[i, j, k]    += a[i] * a[j] * a[k]          end   # R2
S3: for l=_, k=_, j=_, i=_; C[i, j, k, l] += a[i] * a[j] * b[k] * b[l]   end   # R3
S4: for k=_, j=_, i=_;      C[i, j, k]    += a[i] * b[j] * a[k]          end   # R4 (b over n)
S5: for j=_, i=_;           C[i, j]       += a[i] - a[j]                 end   # non-commutative
S6: for j=_, i=_;           C[i, j]       += a[i] * b[j]                 end   # R6 (b over n)
```

**What to count.**

- Use a counting element type: a `struct` wrapping `Float64` whose `Base.:+` and
  `Base.:*` increment global counters.
- The prediction is about innermost-statement executions: one `+` per statement
  into a zero-initialised output.
- Report the raw `*` count as well. An r-factor product costs r − 1
  multiplications unless partial products are hoisted.
- If Finch rejects the custom element type, print the generated code
  (`@finch_code`) and count the trips of the canonical loop nest analytically.
- Count stored cells twice: as the size of the output tensor SySTeC returns, and as
  the number of cells its kernel writes before any copy-to-full step.

**The falsifiable claims, with the numbers at n = 7, 11, 23, 45.** S3 is at the R3
(n, m) pairs; S1' multiplies the S1 counts by p. Each claim is refuted by a
measured value different from the prediction.

| Kernel | Predicted statement executions | Predicted returned cells (from the paper's text) | Refuted if |
|---|---|---|---|
| S1 | C(n+1,2): 28, 66, 276, 1035 | n^2 (dense copy): 49, 121, 529, 2025 | executions ≠ C(n+1,2), or returned cells ≠ n^2 |
| S1' | C(n+1,2) · p | n^2 | executions ≠ C(n+1,2)·p, or returned cells ≠ n^2 |
| S2 | C(n+2,3): 84, 286, 2300, 16215 | n^3 | executions ≠ C(n+2,3) (n^3 included), or returned cells ≠ n^3 |
| S3 | C(n+1,2)·C(m+1,2): 1848, 1848, 18216, 285660 | n^2 m^2 | executions ≠ C(n+1,2)·C(m+1,2), or returned cells ≠ n^2 m^2 |
| S4 | C(n+1,2)·n: 196, 726, 6348, 46575 (see note below) | n^3, axes in source order (i, j, k) | executions = n^3 (or any other value ≠ C(n+1,2)·n), or the output axes reordered (the section 4 failure) |
| S5 | n^2 (no symmetry class covers it; antisymmetry is SySTeC's future work) | n^2 | executions ≠ n^2 with a correct result |
| S6 | n^2 | n^2 | executions ≠ n^2 (`a`, `b` distinct: any quotient) |

Note on S4. The prediction is an inference, not a sourced statement.
`plan-uniform-optimality.md` §7.1 describes SySTeC's symmetrization: apply every
permutation, then normalize by sorting commutative operands, so that equal
right-hand sides merge. That procedure would make `a[i]*b[j]*a[k]` and
`a[k]*b[j]*a[i]` equal. But `plan-paper-1-comoment.md` §8.2 records that the paper
states no detection rule for repeated operands. The surface calculus's n^3 is the
alternative this measurement would discriminate.

Two outcomes would bear on the theory rather than on SySTeC:

1. **Executions below the orbit count, with a correct output for every input.**
   This would not refute the lower bound. It would show the program is outside the
   oracle model. SySTeC's kernels are built-in commutative operators, so `*` is
   interpreted, not opaque. Such a count belongs to the item-7 scope boundary
   (structured kernels may beat the bound).
2. **Attainment of the orbit count on S1–S3.** This is predicted from SySTeC's own
   SSYRK numbers, and would confirm T2 (attainment) in a second, independent
   system. Section 6 already confirms it for `dsyrk` at r = 2 (cells measured, work
   documented).

S4 needs no experiment to show that the {id, (1 3)} quotient can be attained in
source order. Enumerate i ≤ k with j free, write (i, j, k) and mirror it to
(k, j, i); that is attainable trivially. What S4 measures is which licence rule
SySTeC applies. That bears on the choice between the two definitions in section 1,
not on the theory.

## 8. Summary

| Row | Predicted | Blade measured (work = cells; CLI `blade run`) | NumPy measured | Other system measured | Verdict |
|---|---|---|---|---|---|
| R1 `(A, A)` comm | C(n+1,2) | C(n+1,2) at 7, 11, 23, 45 | n^2 (the orbit count only with hand-written indices) | BLAS `dsyrk` (k = 1): C(n+1,2) cells written, n^2 returned; work C(n+1,2)·k documented, not measured | match |
| R2 `(A, A, A)` comm, Poly and lambda | C(n+2,3) | C(n+2,3) | n^3 | — | match |
| R3 `(A, A, B, B)` two comm groups | C(n+1,2)·C(m+1,2) | same | n^2 m^2 | — | match |
| R4 `(A, B, A)` comm(x,y,z) / comm(x,z) | n^3 | C(n+1,2)·n; type reorders the axes to (A, A, B) | n^3 | SySTeC S4 specified, not run | **mismatch**; declared symmetry broken in source coordinates (both lanes); access violation at mixed extents |
| R4c `(A, B, A)` no comm | n^3 | n^3 | n^3 | — | match |
| R5 `(A, A)` no comm | n^2 | n^2, BL4010 warning | n^2 | — | match (no undeclared quotient) |
| R6 `(A, B)` comm | n^2 | n^2, plan declines with reason | n^2 | — | match |
