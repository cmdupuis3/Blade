# LLVM-lane acceptance probes, group A (structure / symmetry)

Author: blade-A agent. Binary: `bin\Release\net10.0\Blade.exe` (compiler 0.20.0) of this worktree.
Every run below is a TINY run (extents 4..11, program time < 20 us). Lanes: `cpp` = default,
`llvm` = `BLADE_LLVM=1`; always `BLADE_BLAS=0`. "accepted" for the llvm lane means: no
`[blade] llvm lane refused` line on stderr AND a `.ll` was emitted (checked with `--verbose`).
Every probe checksum below was also recomputed independently in Python (stdlib) and agrees to
all 15 printed digits. The probes used the FIRST (superseded) generator, two affine LCG rounds;
the kernels in `bench/kernels/` use the corrected three-step generator (section 6).

## 1. Acceptance table

| idiom | cpp | llvm | notes |
|---|---|---|---|
| (a) gram, `method_for(xt, xt) <@> lambda(x: T^1, y: T^1) where comm(x, y) -> prodsum(x, y)` | OK | OK | packed C(p+1,2), triangular; checksum 65.5210717946945 both lanes (n=7, p=5) |
| (a) same with `reduce(x * y, (+))` body | OK, but SLOW SHAPE (allocates a length-n temporary per output cell, F2) | OK (fused, no per-cell alloc) | use `prodsum` |
| (a) + `where comm(x, y), omp(x: 1)` | OK, `BLADE_OMP_PARALLEL_FOR_DYNAMIC` on the outer triangular level | accepted but SERIAL: the lane drops every omp licence silently, no notice (F1) | |
| (b) rank-3 comoment over columns, `lambda(a: T^1, b: T^1, c: T^1) where comm(a, b, c) -> prodsum(a, b, c) / n` | OK | OK | packed C(p+2,3), triangular levels 1,2 "speedup x6", omp on level 0 |
| (b) scalar variant `method_for(v, v, v) <@> lambda(x, y, z) where comm(x, y, z) -> x * y * z` | OK | OK | 10.0 both lanes |
| (b) centering by a row map `method_for(x) <@> lambda(col: T^1) -> col - reduce(col, (+)) / 7.0` | OK | REFUSED: `the IR node IRApplyCombinator in value position -- no arm in the llvm lane` (F3) | use `let mu = reduce(x, (+)) / 7.0` + `method_for(range<Var>, range<Obs>) <@> lambda(j, i) -> x(j, i) - mu(j)`: OK both |
| (c) antisym `method_for(v, v) <@> lambda(p, q) where anticomm(p, q) -> f(p) g(q) - f(q) g(p)` (sin/cos) | OK | OK | packed strict C(n,2), no diagonal; 1.84451636818292 both lanes |
| (c) checksum `decompact(a * a, 0)` | WRONG VALUE 0.0 | WRONG VALUE 0.0 | compiler bug F4 (also the interpreter); use `decompact(a, 0) * decompact(a, 0)` or a gather |
| (d) `reduce(reduce(method_for(P, P) <@> comm-kernel, (+)), (+))` | REFUSED BL3999 (fold over compact output) | same | by design (docs/plans/plan-compact-sym-folds.md, P3 unbuilt) |
| (d) same with `reduce(w, (+), axes = 2)` | REFUSED BL3999 "axes = 2 exceeds the operand's rank 1" | same | the rank dichotomy of the same plan |
| (d) `|> compute` + `reduce(reduce(decompact(w, 0), (+)), (+))` | OK | OK | but materializes n(n+1)/2 then n^2 cells: not viable at benchmark n (memory) |
| (d) dense, no `comm`: `reduce(reduce(method_for(p, p) <@> lambda ..., (+)), (+))` | OK (fused, no n^2 array) | OK | full n^2 iteration; 22.861342802736 both |
| (d) dense with `axes = 2` | OK | OK | the charter's "axes = 2 is refused" no longer holds for this shape nor for a materialized rank-2 array |
| (d) triangle domain `static struct Pair {...} where i < j`, deferred `reduce(w, (+))` in a top-level block | REFUSED BL7004 "reduce over a deferred computation is not supported for ... sparse inputs yet" | same | inside a FUNCTION the same reduce is accepted (it materializes, see (e)) |
| (d) triangle domain with `|> compute` | OK (sparse key table + hash index + value array of n(n-1)/2) | REFUSED: `a loop level with a runtime extent (the llvm lane bakes extents)` | 22.861342802736 (cpp) |
| (e) band `static struct Band { i: Int64<min=0, max=n-1>, j: ... } where abs(i - j) <= w`, `reduce(method_for(range<Band>) <@> ..., (+))` in a function | OK (materializes, F5) | REFUSED: `a loop level with a runtime extent` | 32.8972854572294 |
| (e) the style-guide spelling `Int<min=0, max=n-1>` with `w` a `let static` | REFUSED BL4001 "an Int32 value meets an Int64 operand in `<=`" | same | `Int<...>` fields are Int32; write `Int64<min=..., max=...>` (F6) |
| (f) two scalars from one input generation: `let (checksum, input_checksum) = run()` (function returning a tuple) | OK | REFUSED: `the IR node IRApp in pack position -- the llvm lane builds packs from tuples, <&>/<&!> legs and reduction joins only` (F7) | also refused: `let r = run()` + `r[0]` |
| (f) TOP-LEVEL BLOCK: `let (checksum, input_checksum) = { let xt = ... ; ...; (a, b) }` | OK | OK | prints exactly the two named lines in both lanes; no array is a top-level binding. ADOPTED |
| (f) LCG as a named `function lcg(k: Int64, seed: Int64) -> Float64` called from `method_for(range<I>, range<J>) <@> lambda(i, j) -> lcg(i * p + j, seed)` | OK | OK | exact; bit-identical with Python for k up to 2.1e9 + seed (section 6) |

## 2. Findings (minimal reproducers inline; all tiny)

**F1 (harness-relevant). The LLVM lane is always serial.** `src/EmitLlvm.fs:3037`: "An `omp`
clause is a LICENCE to parallelize, not a demand: emitting the serial body answers the same
values." No refusal line, no notice, no plan entry: the `.ll` of a `where comm(x, y), omp(x: 1)`
gram has no `__kmpc`/GOMP call. Consequence: in the `mt8` regime the `blade-llvm` arm is a
serial build and must be reported as such (or the mt8 Blade headline is `blade-cpp`). The
harness cannot detect this from stderr.

**F2 (C++-lane performance). `reduce(x * y, (+))` inside a comm row kernel allocates.** The
kernel `lambda(x: T^1, y: T^1) where comm(x, y) -> reduce(x * y, (+))` is emitted as a call to
a lambda that `new`s an extents array, allocates a length-n `Array<double,1>`, fills `x[i]*y[i]`,
folds it, and frees both -- once per output cell (seen in `blade emit` of
`tests/fixtures/llvm/bench_sym_gram.blade`, too). `prodsum(x, y)` (and `prodsum(a, b, c)`)
is emitted as an inline fused dot loop. The LLVM lane fuses both spellings. The kernels use
`prodsum`, which is the documented dot/contraction intrinsic, so this is an equally natural
idiom, not a dodge -- but the reduce-of-product spelling is a C++-lane cliff worth fixing.
(Side note: `tests/fixtures/llvm/bench_sym_gram.blade` no longer compiles at all -- its
`0.5 * ((i * 31 + k) % 7)` is BL3020 under the int/float rule.)

**F3 (LLVM refusal). A T^1 -> T^1 row map.**
```
type Obs = Idx<7>
type Var = Idx<4>
function probe3() -> Float64 = {
    let x = method_for(range<Var>, range<Obs>) <@> lambda(j, i) -> 1.0 + 0.25 * Float64(i * 4 + j) |> compute
    let xc = method_for(x) <@> lambda(col: T^1) -> col - reduce(col, (+)) / 7.0 |> compute
    let s = method_for(xc, xc, xc) <@> lambda(a: T^1, b: T^1, c: T^1) where comm(a, b, c) -> prodsum(a, b, c) |> compute
    s((0 : Var), (0 : Var), (0 : Var)) + s((1 : Var), (2 : Var), (3 : Var))
}
let checksum = probe3()
```
llvm: `the IR node IRApplyCombinator in value position -- no arm in the llvm lane`. The
explicit-index centering (table row) is accepted and is the spelling the kernels use.

**F4 (SILENT WRONG VALUE, all three lanes). The elementwise square of an antisymmetric array
keeps the antisymmetric class.**
```
let A = [1.0, 2.0, 4.0]
let R = method_for(A, A) <@> lambda(x, y) where anticomm(x, y) -> x - y |> compute
let S = R * R
let s10 = S(1, 0)
let total = reduce(reduce(decompact(S, 0), (+)), (+))
```
prints `s10 = -1.0` and `total = 0.0` in cpp, llvm and the interpreter; the right answers are
`1.0` and `28.0`. `R * R` is even under the sign flip ((-a)(-b) = ab), so the result is
SYMMETRIC (with a zero diagonal), but it is typed and stored `AntisymIdx` and every mirrored
read is negated. The unary analogue `R + 1.0` IS caught (BL4015 "UNKNOWN sign parity"), so
the parity deduction gets binary self-products wrong (odd * odd is treated as odd).
Work-around-free spelling used by the kernel: `decompact(a, 0) * decompact(a, 0)` (dense
first) or a gather `method_for(range<I>, range<I>) <@> lambda(i, j) -> a(i, j) * a(i, j)`.

**F5 (C++-lane shape of the enumerable domain).** `method_for(range<Band>) <@> ...` emits the
closed-form bounds `j in [max(0, i - w), min(n - 1, i + w)]` (no predicate -- as advertised),
but into a `std::vector` of key pairs (push_back), then a `sparse_index_t` (hash index), then a
materialized `Sparse<double,2>` value array that `unhash`es both keys per cell, then a separate
fold pass. A deferred `reduce` over it is NOT fused with the enumeration. Each read `v((i : Pts))`
also carries a runtime BL8006 range check although the field bounds prove it. Memory is
~24+ bytes per domain cell plus the hash index: for a band, n(2w+1) cells; for a triangle,
n(n-1)/2 cells, which rules the triangle domain out for A4 at benchmark n. The LLVM lane
refuses the domain outright ("a loop level with a runtime extent").

**F6 (doc spelling).** The CLAUDE.md style-guide row spells the band as
`static struct Band { i: Int<min=0, max=n-1>, j: Int<min=0, max=n-1> } where abs(i - j) <= w`;
with `let static w = 2` that is BL4001 (the `Int<...>` fields are Int32, `w` is Int64). With
a literal `<= 2` it compiles. The fix is to declare the fields `Int64<min=0, max=n-1>`.

**F7 (LLVM refusal). A function returning a tuple, destructured at top level.**
```
type I = Idx<4>
function run() -> (Float64, Float64) = {
    let v = method_for(range<I>) <@> lambda(i) -> 0.5 * Float64(i) |> compute
    (reduce(v, (+)), 1.0)
}
let (checksum, input_checksum) = run()
```
llvm: `the IR node IRApp in pack position`. A top-level BLOCK that returns the tuple is
accepted in both lanes (adopted).

**F8 (harness-relevant, clocks).** C++ lane: `std::chrono::high_resolution_clock` (libstdc++:
the system clock, not steady). LLVM lane: `blade_now()` in the shim = `timespec_get(TIME_UTC)`
returned as seconds-since-epoch in a double, so its resolution is 2^-22 s ~ 0.24 us (visible in
the printed times: 2.384e-06 = 10 * 2^-22). Neither is monotonic in the strict sense; both are
fine for timings >= 1 ms. Both lanes print floats with 15 significant digits (`%.15g` /
`setprecision(15)`), so the harness must compare numerically, never textually, against the
Fortran's 17 digits.

**F10 (answers design-decisions O2 / D8: the serial C++ build HAS `-fopenmp`).**
`src/Build.fs:882` sets `ompFlag = "-fopenmp"` unconditionally and line 1001 passes it on
every release compile, `BLADE_OMP_THREADS=1` or not. What the knob changes is the EMISSION:
the thread pragma is replaced by a comment (`// [omp] requested but emitted serial:
BLADE_OMP_THREADS=1 ...`). Evidence from a kept serial build of the gram probe: the `.cpp`
has `#include <omp.h>` and no `BLADE_OMP_PARALLEL*` macro; the `.exe` imports no
`libgomp-1.dll` (MinGW ld drops an import nothing references), while the mt8 build imports
it. Consequences: (i) `_OPENMP` IS defined in the serial build, so any `BLADE_OMP_SIMD` /
`BLADE_OMP_SIMD_REDUCTION` macro the emitter writes DOES expand there; D8's argument "a
serial build without -fopenmp is safe" has a false premise -- reorder-freedom of a serial arm
must be checked on the emitted `.cpp` (no `SIMD_REDUCTION` macro), not on the flag. (ii) The
Fortran serial mirror is `-fopenmp-simd` (gfortran) / `/Qopenmp-simd` (ifx): `omp simd` hints
live, no threads. For the group-A kernels the only loop hints emitted in strict are
`BLADE_IVDEP` (`GCC ivdep`) and no `omp simd` macro at all (checked per kernel, section 7).

**F11 (C++-lane cost, design-decisions O4).** Yes: `reduce(x * y * z, (+))` in a row kernel
allocates and frees a length-n temporary per output cell (the F2 shape); `prodsum(a, b, c)`
streams (`__ps += a[t] * b[t] * c[t]` inline). The same holds for ANY elementwise op on T^1
kernel parameters: `prodsum(a - b, a - b)` in the Matern kernel allocates two length-3
temporaries (4 heap calls) per cell. The LLVM lane fuses all of them. The kernels spell the
contraction `prodsum` (documented, fused) and the pair distance by components over a
concretely typed point (`a: Array<Float64 like Dim>`, `a((0 : Dim))`...), which also removes
the BL8006 literal-bound checks that `a(0)` on a `T^1` parameter carries.

**F12 (checksum spelling).** README 7.2's per-row nest
`reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> S(i, j) * S(i, j), (+)), (+))`
streams in BOTH lanes with NO allocation at all in the C++ lane at this binary (each read is a
`canon_fold<2>` + offset); the 2-D `method_for(range<I>, range<I>)` gather allocates a dense
p x p plus a p-vector in C++; `decompact` allocates p x p and sorts the index pair per cell.
The kernels use the per-row nest.

**F13 (spec correction: an index cast does not prove a literal on an abstract parameter).**
The antisym and pairkernel specs say `a((0 : Cx))` / `a((0 : Dx))` is the check-free
subscript. Not on a `T^1` / `T^2` parameter: its extent is abstract, so the C++ lane still
emits `if (!(0 < a.extents[0])) panic("BL8006" ...)` per read -- for antisym inside the
`for (__pt ...)` loop CONDITION of each prodsum, for pairkernel 6 checks per cell. Typing
the parameter concretely (`a: Array<Float64 like Dim>`, `a: Array<Float64 like Comp, Obs>`)
removes every check (0 BL8006 in the emitted .cpp), identical checksums.

**F14 (REGIME-RELEVANT: the LLVM lane does not contract FMAs by default).**
`src/EmitLlvm.fs:165-184`: `BLADE_FP_CONTRACT` unset means OFF in the LLVM lane ("THE
POLARITY IS DELIBERATELY OPPOSITE TO THE g++ LANE"), while the C++ lane passes
`-ffp-contract=fast` by default. Evidence (serial smoke builds, `objdump -d`): gram C++ exe
has scalar `vfmadd...sd` in its dot loop; the LLVM exe has ZERO fused multiply-adds (gram,
comoment3, antisym all 0), only `vmulsd`/`vaddsd`. With `BLADE_FP_CONTRACT=fast` the LLVM
lane marks 7 `fmul/fadd contract` in the gram `.ll` and the exe has 23 `vfmadd...sd` + 31
`vfmadd...pd`, same checksum to 15 digits. So README §3's "strict = default
`BLADE_FP_CONTRACT`" gives the headline `blade-llvm` arm a DIFFERENT contraction regime from
blade-cpp, gfortran (`-ffp-contract=fast`) and ifx: it computes every dot product as
separate multiply and add. Values stay inside 1e-12, but it is a per-FMA latency/throughput
handicap on exactly the group-A inner products. The orchestrator should decide whether the
strict LLVM arm sets `BLADE_FP_CONTRACT=fast` (my recommendation: yes, it is the same
"contraction only, no reassociation" licence every other arm has) and record it.

**F9 (harness-relevant).** I hit the group-B finding myself: the LLVM lane ignores
`--print` / `BLADE_PRINT` and prints every top-level binding (array ones included). Hence the
top-level-block shape: the only top-level bindings are the two scalars.

## 3. Clock placement

- C++ (`blade emit` with `BLADE_PRINT=checksum,input_checksum`): `auto __blade_t0 = TIME;` then
  the whole top-level block (input generation, kernel with its `BLADE_OMP_PARALLEL_FOR_DYNAMIC`
  nest, checksum folds, `std::make_tuple`, the two `std::get`s), then `auto __blade_t1 = TIME;`,
  then the prints. The kernel is inside.
- LLVM (`--verbose`, the kept `.ll`): in `@main`, `%r1 = call double @blade_now()` precedes the
  first `blade_alloc_cells` and every loop; `@blade_now()` is called again after the last fold
  loop and before `blade_print_completed` / `blade_print_f64`. The kernel is inside (inlined
  into `@main` for the block form; for the function form it is `call @bl_run_N` between the
  two clock calls).

## 4. `blade plan` excerpts (what the derived structure looks like)

gram (`where comm(x, y), omp(x: 1)`, prodsum):
```
[symmetric-storage v1] __v21: applied [packed SymSymmetric storage over 2 levels (one canonical cell per orbit); operands: xt, xt]
[triangular-iteration v1] __v21: applied [triangular levels 1; symcom states: SCNeither, SCCommutative; speedup x2]
[blas-routing v1] __v21: declined -- BLAS gate off (...) [Syrk (L3, d)]
[omp v1] __v21: applied [BLADE_OMP_PARALLEL_FOR_DYNAMIC; loop nest level 0 (__i0)]
```
with `BLADE_OMP_THREADS=1`: `[omp v1] ...: declined -- BLADE_OMP_THREADS=1 -- this build emits
no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)`.

rank-3 comoment: `packed SymSymmetric storage over 3 levels`, `triangular levels 1, 2; symcom
states: SCNeither, SCCommutative, SCCommutative; speedup x6`, `omp ... loop nest level 0`.

antisym: `packed SymAntisymmetric storage over 2 levels`, `triangular levels 1 ... speedup x2`
(strict: n - 1 - i cells in row i, C(n,2) in all).

band / triangle domain: no plan entries (the enumeration is not an optimizer decision);
the emitted C++ comment reads `// enumerable domain Band: 49 solution(s) in closed form, lex order`.

## 5. Idiomatic spellings that work in both lanes (adopted for the kernels)

```blade sketch
function lcg(k: Int64, seed: Int64) -> Float64 = { ... }      // section 6
let (checksum, input_checksum) = {
    let xt = method_for(range<Var>, range<Obs>) <@> lambda(j, i) -> lo + (hi - lo) * lcg(i * P + j, seed) |> compute
    let c = method_for(xt, xt) <@> lambda(x: T^1, y: T^1) where comm(x, y), omp(x: 1) -> prodsum(x, y) |> compute
    (reduce(reduce(decompact(c * c, 0), (+)), (+)), reduce(reduce(xt, (+)), (+)))
}
```
- A compact (packed) result cannot be folded (BL3999, by design); the logical fold is
  `decompact` first. For rank 3, `decompact(decompact(m, 0), 1)` or the gather
  `method_for(range<V>, range<V>, range<V>) <@> lambda(a, b, c) -> m(a, b, c) * m(a, b, c)`.
  The checksum is therefore the sum over the LOGICAL (full, mirrored) tensor -- the Frobenius
  norm squared -- not over the packed cells. Its cost is O(p^2) / O(p^3), negligible next to
  the kernels' O(n p^2) / O(n p^3).

## 6. The generator (charter v2)

```
function lcg(k: Int64, seed: Int64) -> Float64 = {
    let h1 = ((k + seed) * 1103515245 + 12345) % 2147483648
    let h2 = (h1 * (h1 / 4096 + 1)) % 2147483648
    let h3 = (h2 * 1103515245 + 12345) % 2147483648
    Float64(h3) / 2147483648.0
}
```
Typechecks with no conversions beyond the final `Float64(h3)`; every intermediate is Int64 and
non-negative. Checked: sum over k = 9973 m + 2147483000 (m < 1000), seed 50000095, of u =
500.346257399302, and lcg(0, 10000019) + lcg(12345678, 20000038) = 0.102654925081879, identical
in cpp, llvm and Python.

Coordinator's cross-language reference, confirmed in BOTH lanes (scratch program, 10 cells,
`x(k) = -1.0 + 2.0 * lcg(k, 10000019)`, k = 0..9):

| quantity | reference | cpp | llvm |
|---|---|---|---|
| u(0) | 0.08579639764502645 | 0.0857963976450264 | 0.0857963976450264 |
| u(1) | 0.020233582705259323 | 0.0202335827052593 | 0.0202335827052593 |
| u(2) | 0.1913897623308003 | 0.1913897623308 | 0.1913897623308 |
| Σ x | -1.58356509916484 | -1.58356509916484 | -1.58356509916484 |
| Σ x² | 3.67314916727079 | 3.67314916727079 | 3.67314916727079 |

`Float64(h3) / 2147483648.0` divides by a power of two (exact); every range in the specs has
`hi - lo` a power of two, so `lo + (hi - lo) * u` is exact and contraction cannot move a bit.

## 7. Group-A kernels written (TASK 2)

> SUPERSEDED VALUES: the checksums in this section were produced with the three-round
> generator (R3). The sources now use variant D (five rounds); current smoke values, pins and
> hand-check values are in section 11.

Files: `bench/kernels/{gram,comoment3,antisym,pairkernel}/<name>.blade`, committed at FULL
size, `blade check` (tier full) clean at full size. Shape common to all four:

- `let static p = ... // SIZE p`, `let static n = ... // SIZE n` (one literal per marked line),
  `type Var = Idx<p>` etc. derived from them; the LCG of section 6 as `function lcg`.
- one top-level block `let (checksum, input_checksum) = { ... }`: inputs `|> compute`d,
  kernel `|> compute`d (G / T / L / K are materialized), the per-row logical checksum nest
  (F12), the input sum, then the tuple. No array is a top-level binding.
- LLVM lane: NO fallback line for any of the four; it does echo the `let static` sizes as
  extra lines (`p = 61`, `n = 31`), which kernels/README.md §3 tells the harness to ignore.
- C++ lane smoke times are ~1 ms because the default (mt) build starts an OpenMP team; the
  LLVM lane (serial, F1) runs the same smoke in 10-40 us.

Verification (smoke sizes from the specs; both lanes printed identical 15-digit values, and
every value equals an independent Python/fsum reference, `scratchpad/ref.py`, rounded to 15
digits):

| kernel | smoke size | checksum | input_checksum | plan (C++ lane) |
|---|---|---|---|---|
| gram | p=61, n=31 | 230166.373255735 | 943.17593226349 | packed SymSymmetric over 2 levels (x, x); triangular levels 1, x2; omp DYNAMIC level 0; blas-routing declined (gate off) [Syrk] |
| comoment3 | p=13, n=11 | 27.4407241925205 | 65.7430054359138 | packed SymSymmetric over 3 levels (x, x, x); triangular levels 1, 2, x6; omp DYNAMIC level 0 |
| antisym | p=23, n=11 | 412.917459757621 | 241.864194787107 | packed SymAntisymmetric over 2 levels (z, z); triangular levels 1, x2; omp DYNAMIC level 0 |
| pairkernel | n=53 | 1055.492982795 | 70.305815611966 | packed SymSymmetric over 2 levels (pts, pts); triangular levels 1, x2; omp DYNAMIC level 0 |

Reference values at trivially small extents (for the Fortran agent; Blade both lanes ==
Python to the digit; 17-digit Python values in parentheses):

| kernel | size | checksum | input_checksum |
|---|---|---|---|
| gram | p=2, n=3 | 0.193603627810172 (0.19360362781017204) | 1.37163939420134 (1.3716393942013383) |
| comoment3 | p=2, n=3 | 0.00373706179635241 (0.003737061796352414) | 1.37163939420134 |
| antisym | p=3, n=2 | 0.146953930861803 (0.14695393086180272) | 3.95103613846004 (3.9510361384600401) |
| pairkernel | n=3 | 5.47062109978202 (5.4706210997820222) | 2.56968463025987 (2.5696846302598715) |

Hand check, gram p=2, n=3: u(0..5) = 0.085796397645026445, 0.020233582705259323,
0.19138976233080029, 0.37148173339664936, 0.48702316777780652, 0.21571475034579635;
X row 0 = u(0..2), row 1 = u(3..5); G00 = 0.044400460842994288, G01 = 0.08301161286090214,
G11 = 0.421723097716458; checksum = G00^2 + 2 G01^2 + G11^2 = 0.193603627810172;
input_checksum = sum of u = 1.37163939420134. Matches both lanes.

Packed element counts the expert tier mirrors (Blade's pool sizes, checked in the `.ll`:
`blade_alloc_cells(i64 3 | 4 | 3 | 6, ...)` at the hand-check sizes = C(3,2), C(4,3), C(3,2),
C(4,2)): gram C(p+1,2) = 1,282,401 at full; comoment3 C(p+2,3) = 848,046; antisym strict
C(p,2) = 720,600; pairkernel C(n+1,2) = 3,133,756. Layout (informational): left-justified
row-major, row i holds j = i..p-1 (antisym j = i+1..p-1), row offset C(p+1,2) - C(p+1-i,2)
(rank 3: `C(p+2,3) - C(p+2-i,3)` for the row, then the triangle within it).

What each kernel's emit actually does (gap-register evidence, C++ lane, mt build):

- **gram**: `BLADE_OMP_PARALLEL_FOR_DYNAMIC` on `for __i0 < p`, inner `for __i1 < p - __i0`,
  each cell an inline scalar dot `__ps += a[t] * b[t]` over the n samples (ascending t, one
  accumulator), stored to the packed row; `[ivdep] declined: kernel body contains an inner
  loop`. NO jam across output cells, NO outer-product order, NO allocation (prodsum). The
  spec's expected "jammed row-fold (`__i0 + 6 <= __jm`)" is NOT in the gram nest: that jam is
  the `[microkernel v1] row-fold jam` the plan applies to dense PARTIAL folds (the input /
  checksum folds of the probes, antisym's input fold here), not to the comm map. So each cell
  is one latency-bound FMA chain of length n, one cell at a time. With `reduce(a * b, (+))`
  instead of `prodsum` the cell becomes a call that allocates, fills and frees a length-n
  temporary (F2): the spec's remark that `reduce(x * y, (+))` "also streams" is not true in
  the C++ lane.
- **comoment3**: dynamic omp on level 0, three-level simplex nest (`__i1 < p - __i0`,
  `__i2 < p - __i1 - __i0`), each cell an inline `__ps += a[t] * b[t] * c[t]` then
  `/ static_cast<double>(n)`: a DIVISION by n once per cell (the spec text says "multiply by
  1/n once"; its own sketch writes `/ Float64(n)`; the Fortran should divide, `t = s / n`, to
  match bit for bit; the difference is <= 1 ulp per cell, inside 1e-12 anyway). No
  allocation. The checksum is the rank-3 per-row nest: no p^2 or p^3 allocation (only X and
  the packed T are allocated).
- **antisym**: dynamic omp on level 0, strict row `__i1 < p - __i0 - 1`, each cell two inline
  dot loops subtracted; no allocation, no BL8006 (concrete row types, F13). The input sum
  `reduce(reduce(reduce(z)))` materializes a (p, 2) and a (p) partial (19 KB at full size,
  negligible; this is where the row-fold jam microkernel shows up).
- **pairkernel**: dynamic omp on level 0, `__i1 < n - __i0`, `BLADE_IVDEP` kept (no inner
  loop), each cell three differences, `r2 = dx*dx + dy*dy + dz*dz` left to right, and a call
  to `matern52` (`std::sqrt`, `blade_libm::exp`). No allocation, no BL8006 (F13).
- Serial builds (`BLADE_OMP_THREADS=1`): the pragma is replaced by the "emitted serial"
  comment; no `BLADE_OMP_SIMD` / `SIMD_REDUCTION` macro anywhere in any of the four, so the
  strict serial arms are reorder-free even though `-fopenmp` is on the line (F10). Only
  `BLADE_IVDEP` (`GCC ivdep`) appears.
- LLVM lane: every kernel and every checksum nest accepted (no refusal line). In `@main`,
  `blade_now()` precedes the first `blade_alloc_cells` and follows the last fold loop, before
  `blade_print_completed`: the whole block is timed. The omp licence is dropped (F1).

Deviations from the spec sketches, each equally natural and documented in the source:
`prodsum` instead of `reduce(x * y * z, (+))` in comoment3 (and gram), because the latter
allocates per cell in the C++ lane (F2/F11; the spec asked to record, not dodge; prodsum is
the documented contraction intrinsic, so I treat it as the equally natural idiom and flag the
choice for the orchestrator); concretely typed kernel parameters in antisym / pairkernel,
because the spec's `(0 : Cx)` cast on `T^k` parameters does not remove the checks (F13);
`let static` size declarations instead of `type X = Idx<N> // SIZE`, so `k = i * n + s`
reads the size from its one marked declaration. (The coordinator confirmed `prodsum` as the
one source for gram and comoment3, 2026-10-17.)

## 8. Gap register: what the emitted inner loops ARE (vs the expert's outer-product order)

Source: `blade emit` of the committed sources at smoke size (C++ lane, mt build; the serial
build differs only in the pragma line), plus `objdump -d` of the serial executables of both
lanes. Identifiers are the emitter's.

**gram** (p=61, n=31 smoke):
```
BLADE_OMP_PARALLEL_FOR_DYNAMIC
for (size_t __i0 = 0; __i0 < 61; __i0++) {
    const size_t __oend___v24_0 = 62 - __i0;
    const size_t __ooff___v24_0 = (1891) - ((__oend___v24_0 * (__oend___v24_0 - 1)) / 2);
    Array<double, 1> __v19____i0 = { __v19.data[__i0], __v19.extents + 1 };
    double* BLADE_RESTRICT __orow___v24 = __opool___v24 + __ooff___v24_0;
    // [ivdep] declined: kernel body contains an inner loop (prodsum/reduce/contains/nested apply), so this header cannot vectorize
    for (size_t __i1 = 0; __i1 < 61 - __i0; __i1++) {
        Array<double, 1> __v19____i1 = { __v19.data[__i1 + __i0], __v19.extents + 1 };
        __orow___v24[__i1] = [&]() { double __ps = 0; for (size_t __pt = 0; __pt < __v19____i0.extents[0]; __pt++) { __ps += __v19____i0[__pt] * __v19____i1[__pt]; } return __ps; }();
    }
}
```
- ONE output cell at a time, ONE accumulator (`__ps`) per cell, the sample loop innermost
  (dot form), ascending s. NO jam across cells (no `__i0 + 6 <= __jm`; that jam is the
  `[microkernel v1] row-fold jam` applied to dense partial folds, never to this map), NO
  outer-product order (s is never outside i/j), NO blocking, NO allocation.
- The dot loop's trip count is the RUNTIME `__v19____i0.extents[0]`, not the constant n:
  the kernel parameter is `T^1`, whose extent is abstract. Typing it `a: Array<Float64 like
  Obs>` makes it `__pt < 31` (checked in a scratch copy: same checksums both lanes, same
  plan); the committed antisym/pairkernel already use concrete types (F13). This bears on
  the "same information" rule (Fortran sees `n` as a parameter); I have NOT changed the
  committed gram/comoment3 (coordinator: no kernel changes without a reviewer's request) --
  flagged for the review round.
- Machine code, serial: the C++ exe contains only scalar FMAs (`vfmadd...sd`, no packed
  FMA), consistent with a latency-bound one-accumulator FMA chain per cell; the LLVM exe
  contains no FMA at all (F14). The strict regime licenses no reassociation, so the dot
  cannot be vectorized as written. (The LLVM exe does contain packed `vmulpd`/`vaddpd`; I
  did not attribute them to loops -- likely the input generation / checksum nests -- so do
  not read that as a vectorized dot without checking.)
- Contrast, expert outer-product order `do s; do j; do i = 1, j: g(i,j) += x(i,s)*x(j,s)`:
  i innermost and contiguous, vectorizable 4-wide FMAs with no reassociation, each cell
  still summed in s order. Blade has no spelling that makes the emitter choose that order;
  it follows from the kernel being a row (`T^1`) function.

**comoment3** (p=13, n=11 smoke):
```
BLADE_OMP_PARALLEL_FOR_DYNAMIC
for (size_t __i0 = 0; __i0 < 13; __i0++) {
    const size_t __oend___v25_0 = 15 - __i0;
    const size_t __ooff___v25_0 = (455) - ((__oend___v25_0 * (__oend___v25_0 - 1) * (__oend___v25_0 - 2)) / 6);
    Array<double, 1> __v19____i0 = { __v19.data[__i0], __v19.extents + 1 };
    for (size_t __i1 = 0; __i1 < 13 - __i0; __i1++) {
        const size_t __ohi___v25_1 = __oend___v25_0 - 1;
        const size_t __oend___v25_1 = __ohi___v25_1 - __i1;
        const size_t __ooff___v25_1 = __ooff___v25_0 + ((__ohi___v25_1 * (__ohi___v25_1 - 1)) / 2) - ((__oend___v25_1 * (__oend___v25_1 - 1)) / 2);
        Array<double, 1> __v19____i1 = { __v19.data[__i1 + __i0], __v19.extents + 1 };
        double* BLADE_RESTRICT __orow___v25 = __opool___v25 + __ooff___v25_1;
        // [ivdep] declined: kernel body contains an inner loop (prodsum/reduce/contains/nested apply), so this header cannot vectorize
        for (size_t __i2 = 0; __i2 < 13 - __i1 - __i0; __i2++) {
            Array<double, 1> __v19____i2 = { __v19.data[__i2 + __i1 + __i0], __v19.extents + 1 };
            __orow___v25[__i2] = ([&]() { double __ps = 0; for (size_t __pt = 0; __pt < __v19____i0.extents[0]; __pt++) { __ps += __v19____i0[__pt] * __v19____i1[__pt] * __v19____i2[__pt]; } return __ps; }() / static_cast<double>(n));
        }
    }
}
```
- Simplex nest i <= j <= k (left-justified), one cell at a time, one accumulator, sample
  loop innermost (2 multiplies + 1 add per term, the multiply-add contracted to one FMA in the
  C++ lane only), division by n once per cell. No jam, no outer-product order, no
  allocation. Trip count again `extents[0]` (runtime), as for gram.
- The checksum is a separate rank-3 nest of p^3 canonicalizing reads (the "checksum tax").
- Expert contrast: `do s; do k; do j <= k; do i <= j` keeps i innermost/contiguous and
  amortizes x(k,s)*x(j,s) across the i loop -- a strength reduction the dot form cannot do
  (Blade recomputes `a[t] * b[t]` for every k).

**antisym** (p=23, n=11 smoke):
```
BLADE_OMP_PARALLEL_FOR_DYNAMIC
for (size_t __i0 = 0; __i0 < 23; __i0++) {
    const size_t __oend___v32_0 = 23 - __i0;
    const size_t __ooff___v32_0 = (253) - ((__oend___v32_0 * (__oend___v32_0 - 1)) / 2);
    Array<double, 2> __v24____i0 = { __v24.data[__i0], __v24.extents + 1 };
    double* BLADE_RESTRICT __orow___v32 = __opool___v32 + __ooff___v32_0;
    // [ivdep] declined: kernel body contains an inner loop (prodsum/reduce/contains/nested apply), so this header cannot vectorize
    for (size_t __i1 = 0; __i1 < 23 - __i0 - 1; __i1++) {
        Array<double, 2> __v24____i1 = { __v24.data[__i1 + __i0 + 1], __v24.extents + 1 };
        __orow___v32[__i1] = ([&]() { double __ps = 0; for (size_t __pt = 0; __pt < 11; __pt++) { __ps += Array<double, 1>{ __v24____i0.data[0L], __v24____i0.extents + 1 }[__pt] * Array<double, 1>{ __v24____i1.data[1L], __v24____i1.extents + 1 }[__pt]; } return __ps; }() - [&]() { double __ps = 0; for (size_t __pt = 0; __pt < 11; __pt++) { __ps += Array<double, 1>{ __v24____i1.data[0L], __v24____i1.extents + 1 }[__pt] * Array<double, 1>{ __v24____i0.data[1L], __v24____i0.extents + 1 }[__pt]; } return __ps; }());
    }
}
```
- Strict triangle j > i, one cell at a time, TWO sequential dot loops of constant trip
  count n (concrete row types), each with one accumulator, subtracted at the end. The two
  dots are not fused into one loop (two passes over the same 4 rows of n doubles per cell),
  no jam, no outer-product order, no allocation, no bound checks.
- Expert contrast: one s loop carrying both products (or the outer-product order over
  `j > i`), vectorized over i.

**pairkernel**: `for __i0 < n` (dynamic omp), `for __i1 < n - __i0` with `BLADE_IVDEP` kept,
per cell three differences, `r2` left to right, a call to `matern52` (`std::sqrt`,
`blade_libm::exp`). Transcendental-bound; loop order is not the question here.

## 9. Committed reproducers (`bench/notes/repro/`)

Each file's header states the expected and observed value or refusal, the lanes affected,
and the command. All were re-run on 2026-10-17 in both lanes and behave as their headers
say.

| file | finding | lanes | kind |
|---|---|---|---|
| `A-F4-antisym-self-product.blade` | F4 | cpp, llvm, interpreter | SILENT WRONG VALUE (`s10 = -1.0`, `total = 0.0`; right: 1.0, 28.0) |
| `A-F2-reduce-product-alloc.blade` | F2 | cpp | per-cell `new size_t[1]` + `allocate` in the kernel lambda |
| `A-F11-rowdiff-alloc.blade` | F11 | cpp | two length-3 temporaries per cell from `prodsum(a - b, a - b)` |
| `A-F3-rowmap-llvm-refusal.blade` | F3 | llvm | `IRApplyCombinator in value position` |
| `A-F5-band-domain-materialized.blade` | F5 | cpp (sparse materialization, BL8006 per read), llvm (`a loop level with a runtime extent`) | shape / refusal |
| `A-F6-int32-domain-bound.blade` | F6 | all | BL4001 on the style-guide band spelling |
| `A-F7-tuple-return-llvm.blade` | F7 | llvm | `IRApp in pack position` |
| `A-F13-cast-on-abstract-param.blade` | F13 | cpp | BL8006 literal checks despite `(0 : Dim)` casts on a `T^1` parameter |

F1 (LLVM drops omp), F10 (`-fopenmp` on serial builds), F12 (checksum spellings) and F14 (no
FMA contraction in the LLVM lane by default) are facts about the build pipeline, shown by any
group-A kernel source with `--verbose`; they need no separate reproducer.

## 10. Round 2 (methodology review M8, M10, M11, M12, M23, M24)

> The pins, validation errors and store-audit addresses in this section are for the R3
> generator and the repro regime; section 11 re-does all of it for variant D and both regimes.
> The qualitative findings (stores survive, march, exp, generation loop, M24 placement)
> are unchanged by the generator switch.

**Concrete parameter types (decision 2026-10-17).** gram and comoment3 now type their
kernel rows `Array<Float64 like Obs>`. Re-verified at smoke size in both lanes: checksums
unchanged (230166.373255735 / 943.17593226349; 27.4407241925205 / 65.7430054359138), plan
unchanged (packed storage, triangular x2 / x6, omp DYNAMIC level 0), the dot loop now
`__pt < 31` / `__pt < 11` (constant), still no allocation or BL8006, `blade check` clean at
full size.

**Pins (M11, M12).** `bench/validation/ref_{gram,comoment3,antisym,pairkernel}.py` (numpy,
independent of Blade: inputs from `benchgen.py`, outputs by matmul / elementwise, sums with
`math.fsum`), `ref_common_A.py` (sizes from each `kernel.json`), `pins_A.py` ->
`bench/validation/pins-A.json` (schema `{kernel: {size: {checksum, input_checksum}}}`;
values are JSON floats in shortest round-trip form, i.e. exactly the double `%.17g` prints).
Run with `C:\Python314\python.exe -P` (numpy is in the user site; the scripts put their own
directory on `sys.path`). All sizes take 2.2 s of single-threaded numpy in total.

| kernel | size | checksum | input_checksum |
|---|---|---|---|
| gram | smoke 61x31 | 230166.37325573518 | 943.17593226348981 |
| gram | full 1601x1001 | 160495998476.09042 | 801015.0192480837 |
| gram | scale 2003x1001 | 251428557106.81345 | 1002383.3215166898 |
| comoment3 | smoke 13x11 | 27.440724192520541 | 65.743005435913801 |
| comoment3 | full 171x201 | 77767.671828577775 | 17118.017567719333 |
| antisym | smoke 23x11 | 412.91745975762109 | 241.86419478710741 |
| antisym | full 1201x401 | 57636283.535468392 | 481618.63132100366 |
| pairkernel | smoke 53 | 1055.4929827949966 | 70.305815611965954 |
| pairkernel | full 2503 | 2256709.1918434082 | 3758.998480131384 |

The smoke pins equal the stdlib reference of section 7 and Blade's printed values (15 digits).
The full-size Blade checksums have NOT been run by me (runner only).

**Cell-by-cell validation (M11).** `bench/validation/dump/<kernel>_dump.blade` (smoke size,
validation only: the same generator, kernel and checksum with the arrays at top level) and
`bench/validation/validate_A.py`, which runs each dump through Blade.exe, parses the printed
arrays and compares (a) the dense LOGICAL output read through the canonicalizing accessor
(both triangles; antisym's sign-tracked lower half and implicit zero diagonal; comoment3's
full p^3 cube), (b) the PACKED storage in Blade's left-justified order against the
reference's canonical cells, (c) both checksums against the numpy pins. Result (both lanes;
the LLVM runs with `BLADE_FP_CONTRACT=fast`; identical numbers in both lanes):

| kernel | dense cells | dense max rel | packed cells | packed max rel | checksum rel | input rel |
|---|---|---|---|---|---|---|
| gram | 3721 | 4.8e-15 | 1891 | 4.8e-15 | 7.6e-16 | 2.4e-16 |
| comoment3 | 2197 | 4.8e-15 | 455 | 4.7e-15 | 1.6e-15 | 0 |
| antisym | 529 | 1.2e-13 (max abs 4.9e-15) | 253 | 4.6e-15 | 2.8e-16 | 1.7e-15 |
| pairkernel | 2809 | 2.9e-15 | 1431 | 2.9e-15 | 3.2e-15 | 6.1e-16 |

Every cell agrees to the 15 printed digits (the ~5e-15 floor is the print precision). The
antisym dense 1.2e-13 is one near-zero lower-triangle cell: numpy computes L(j,i) as
`dot(Xj,Yi) - dot(Yj,Xi)` directly, Blade returns `-L(i,j)`; same absolute error (4.9e-15)
as everywhere else, and the reference zero cells are zero in Blade (0 non-zero). Exit 0.

**Store audit (M10).** Full-size sources compiled (not run) in three builds per kernel:
`cpp-serial` (`BLADE_OMP_THREADS=1`), `cpp-mt8` (knob unset), `llvm` (`BLADE_LLVM=1`,
`BLADE_FP_CONTRACT=fast`; omp is dropped in this lane, F1); `objdump -d` from ucrt64. No LLVM
refusal for any kernel. In every build the production loop stores each computed cell to the
packed pool; none was eliminated:

| kernel | build | production loop (store after the cell's arithmetic) |
|---|---|---|
| gram | cpp-serial | `main+0x31f..`: dot = `vmulpd` 4-wide products, 4 in-order `vaddsd` per iteration (order-preserving, NOT contracted), tail `vfmadd231sd`; `vmovsd %xmm0,(%r11,%r9,8)` @14000a586; outer `cmp $0x641` (1601) |
| gram | cpp-mt8 | `main._omp_fn.0` (`GOMP_loop_nonmonotonic_dynamic_next`): same dot; `vmovsd %xmm0,(%r11,%r9,8)` @140001552 |
| gram | llvm | 64x64 BRICK tiles (`cmp $0x40`, 25 tiles of 64 + remainder) over the triangle; dot = scalar `vfmadd132sd` chain unrolled 13x (1001 = 13 x 77), one accumulator; `vmovsd %xmm0,(%r10,%r9,8)` @140001936 (and @1400017b5, @140001aaa in the edge tiles) |
| comoment3 | cpp-serial | simplex nest, dot = two `vmulpd` + 4 in-order `vaddsd`, `vdivsd` by n; `vmovsd %xmm0,(%rdi,%r10,8)` @14000a8dc |
| comoment3 | cpp-mt8 | `main._omp_fn.0.constprop.0`: `vdivsd` then `vmovsd %xmm0,(%rdi,%r11,8)` @140001a9a |
| comoment3 | llvm | scalar, 3x unrolled `vmulsd` + `vfmadd132sd`, `vdivsd`; `vmovsd %xmm1,(%r14,%r15,8)` @140001842 (and @1400017cb, @1400018cd) |
| antisym | cpp-serial | two dot loops (vectorized products, in-order adds), `vsubsd`; `vmovsd %xmm0,(%r14,%r11,8)` @14000a98d |
| antisym | cpp-mt8 | `main._omp_fn.0`: `vsubsd` then `vmovsd %xmm0,(%rdi,%r11,8)` @1400015cd |
| antisym | llvm | scalar `vfmadd` chains, `vsubsd`; `vmovsd %xmm0,0x57f6c0(%rax,%r12,8)` @14000182b |
| pairkernel | cpp-serial | 3 `vsubsd`, `vmulsd`+2 `vfmadd132sd` (r2), `vsqrtsd`, `vdivsd`, `call exp`; `vmovsd %xmm11,-0x8(%rbp)` @14000a6aa |
| pairkernel | cpp-mt8 | `main._omp_fn.0`: same; `vmovsd %xmm10,(%rsi,%r12,8)` @140001ad4 |
| pairkernel | llvm | same sequence; `vmovsd %xmm0,(%rbx,%r13,8)` @140001771 |

Observations for the gap register: (i) the C++ lane's strict dot is NOT an FMA chain at full
size: with the constant trip count g++ vectorizes the PRODUCTS and keeps the sum in order
(4 dependent `vaddsd` per 4 terms), so the contraction licence applies only to the tail
term; (ii) the LLVM lane BRICKS gram's triangle into 64x64 tiles (simplex-blocked) and keeps
a one-accumulator scalar FMA chain per cell; the C++ lane walks rows; (iii) the checksum's
canonicalizing read in the C++ lane calls `std::__insertion_sort` per cell (gram
`__lambda_68`), which is the mechanism of the checksum tax there.

**Resolved targets and vector math (M23).** `g++ -march=native` resolves to `znver3`
(`-Q --help=target`); clang 22 `-march=native` to `-target-cpu znver3`. pairkernel's `sqrt`
is the inline `vsqrtsd` in both lanes; `exp` is a scalar call to the UCRT `exp`
(`__imp_exp` from `api-ms-win-crt-math-l1-1-0.dll`) in both lanes, once per cell; no vector
math library (no SVML/libmvec) in any Blade build. The LLVM exe also imports `pow` from the
same DLL (shim). gram/comoment3/antisym call no math library in their kernels.

**Input generation (M8).** Inside the timed region, both lanes generate the inputs with a
SCALAR loop: no packed 64-bit multiplies (AVX2 has no `vpmullq`), `imul` by 0x41c64e6d,
`and` for the mod 2^31, a shift for `/ 4096`, one `vcvtsi2sd` per cell. The LLVM lane
strength-reduces the first round (`(k + seed) * A` becomes an induction add; no
`imul $0x41c64e6d` left in pairkernel). The C++ lane inlines `lcg` (no call). Neither lane
threads the generation (no licence on the generator).

**Which loop is threaded (M24).**
- gram: `[omp v1] applied [BLADE_OMP_PARALLEL_FOR_DYNAMIC; loop nest level 0 (__i0)]` on G's
  triangular nest -- the row (variable) loop of the KERNEL, `schedule(dynamic)` (the mt8
  exe's `main._omp_fn.0` holds the dot loop and the pool store). Input generation, the
  checksum nest and the input sum are serial. So: parallel kernel, serial preparation and
  checksum. In the LLVM lane nothing is threaded (F1).
- comoment3: same placement, level 0 (i) of the simplex nest; the outer level carries
  C(p+1-i, 2) cells for row i, so the dynamic schedule matters. Generation and the p^3
  checksum nest are serial.
- antisym: level 0 (i) of the strict triangle, dynamic; generation, checksum and the input
  sum's partial folds are serial.
- pairkernel: level 0 (i) of K's triangle, dynamic, around the sqrt/exp cells; generation
  (3n cells) and the n^2-read checksum are serial. The checksum is the largest serial share
  among the four (an n^2 canonicalizing read pass against C(n+1,2) exp evaluations).
None of the four has a parallel input loop masquerading as a parallel kernel; the opposite
holds (serial preparation beside a parallel kernel).

## 11. Generator variant D and the licensed / repro regimes (current values)

**Generator.** All four kernel sources and all four dumps now use variant D: after h3,
`let h4 = (h3 * h3 / 65536) % 2147483648`, `let h5 = (h4 * 1103515245 + 12345) % 2147483648`,
`Float64(h5) / 2147483648.0` (h3 * h3 < 2^62, exact in Int64). Checked in both lanes:
h5 at seed 10000019 = 408748897, 189020261, 1945512958 for k = 0, 1, 2 (the coordinator's
reference); the 10-cell selfcheck x(k) = -1 + 2u(k), k = 0..9, gives sum = -3.1010268041864,
sum of squares = 3.4272198544854 in cpp, llvm and numpy (benchgen "R5"). The group-A
references go through `benchgen.gen_array(..., variant="R5")`; `ref_common_A.py` pins the
variant explicitly and warns if benchgen's default differs (it still says "R3" as of this
writing -- blade-B's file).

**Smoke, both lanes, = numpy to 15 digits** (also re-run after the concrete typing):

| kernel | smoke | checksum | input_checksum |
|---|---|---|---|
| gram | p=61, n=31 | 237924.717413045 | 952.456307353452 |
| comoment3 | p=13, n=11 | 45.0076154756788 | 71.5238295937888 |
| antisym | p=23, n=11 | 390.607241954913 | 239.513063936029 |
| pairkernel | n=53 | 1051.80125396941 | 73.6780804120935 |

**Pins (`pins-A.json`, regenerated, 17 digits; every entry also carries `"sizes"`, the exact
`{param: int}` map it was computed at, e.g. `{"p": 1601, "n": 1001}`, so the harness can refuse
a stale pin):**

| kernel | size | checksum | input_checksum |
|---|---|---|---|
| gram | smoke | 237924.71741304471 | 952.45630735345185 |
| gram | full 1601x1001 | 160344246688.88895 | 800824.01562452829 |
| gram | scale 2003x1001 | 251075141966.11023 | 1002033.4638998141 |
| comoment3 | smoke | 45.007615475678833 | 71.523829593788832 |
| comoment3 | full 171x201 | 80611.461358387489 | 17220.99644652009 |
| antisym | smoke | 390.60724195491264 | 239.51306393602863 |
| antisym | full 1201x401 | 57760462.7106089 | 481149.43540512957 |
| pairkernel | smoke | 1051.8012539694091 | 73.67808041209355 |
| pairkernel | full 2503 | 2266701.7518547112 | 3746.8739239438437 |

**Hand-check sizes (for the Fortran agent; Blade both lanes == numpy):**

| kernel | size | checksum | input_checksum |
|---|---|---|---|
| gram | p=2, n=3 | 1.9556727107566534 | 2.5198342269286513 |
| comoment3 | p=2, n=3 | 0.19425612476176321 | 2.5198342269286513 |
| antisym | p=3, n=2 | 0.4678318543348865 | 6.1942301015369594 |
| pairkernel | n=3 | 3.4729403937976011 | 4.7320134812034667 |

gram 2x3 by hand: u(0..5) = 0.19033853756263852, 0.088019418064504862, 0.90595006849616766,
0.46552763972431421, 0.29578711045905948, 0.5742114526219666 (u(0) = 408748897 / 2^31);
G00 = 0.86472170344610888, G01 = 0.63484976430785522, G11 = 0.63392479038323968;
G00^2 + 2 G01^2 + G11^2 = 1.95567271075665.

**Cell-by-cell validation, both regimes** (`validate_A.py --lanes cpp,llvm --regime
licensed|repro`; licensed = `BLADE_FP_REASSOC=1`, LLVM always with `BLADE_FP_CONTRACT=fast`):

| kernel | regime | lane | dense max rel | packed max rel | checksum rel | input rel |
|---|---|---|---|---|---|---|
| gram | both | both | 4.7e-15 | 4.7e-15 | 1.2e-15 | 1.2e-16 |
| comoment3 | both | both | 4.6e-15 | 4.6e-15 | 7.9e-16 | 4.0e-16 |
| antisym | repro | both | 6.0e-15 | 4.3e-15 | 8.7e-16 | 1.5e-15 |
| antisym | licensed | cpp | 1.2e-13 | 1.2e-13 | 8.7e-16 | 1.5e-15 |
| antisym | licensed | llvm | 6.0e-15 | 4.3e-15 | 8.7e-16 | 1.5e-15 |
| pairkernel | both | both | 2.4e-15 | 2.4e-15 | 8.7e-16 | 7.7e-16 |

Worst over everything 1.2e-13 (licensed, cpp, antisym: one near-zero cell whose two
reassociated dot products cancel; absolute error 4.9e-15 like every other cell). Exit 0 in
both regimes.

**F15: the licensed regime DOES change the emission of every group-A kernel (correction to
"expect no emission change").** The inner `prodsum` IS a licensed fold under
`BLADE_FP_REASSOC=1`. gram at smoke size, serial C++ emit, repro vs licensed:
```
repro:    __orow___v27[__i1] = [&]() { double __ps = 0; for (size_t __pt = 0; __pt < 31; __pt++) { __ps += __v21____i0[__pt] * __v21____i1[__pt]; } return __ps; }();
licensed: __orow___v27[__i1] = [&]() { const size_t __pn = 31; double __ps = 0; BLADE_OMP_SIMD_REDUCTION(+:__ps) for (size_t __pt = 0; __pt < __pn; __pt++) {     __ps = __ps + __v21____i0[__pt] * __v21____i1[__pt]; } return __ps; }();
```
and the three checksum/input folds become `// reduce over computation: accumulator loop (omp
simd reduction, BLADE_FP_REASSOC, ...)` with `BLADE_OMP_SIMD_REDUCTION`. The same happens in
comoment3, antisym (both dots) and pairkernel (its folds only; the kernel has no fold), and
in antisym the input-sum row-fold jam is replaced by simd-reduction accumulators. `blade
plan` is IDENTICAL under both knobs for gram, comoment3 and pairkernel (antisym only loses
its `[microkernel v1] row-fold jam` line): the plan does not report the reassociation, so
the write-up must cite the emit, not the plan. In the serial build the macro expands
(`-fopenmp` is always on, F10), so the licensed SERIAL arm is reassociated by design.
Machine code (full-size gram, `objdump -d`):
- C++ licensed: the dot becomes `vmulpd` + `vaddpd %ymm` into ONE 4-lane vector accumulator
  (no FMA: g++ does not contract the simd-reduction body), horizontal sum after the loop;
  repro: `vmulpd` 4-wide products + four in-order `vaddsd`.
- LLVM licensed: `fadd reassoc nsz contract` on the accumulator chains (5 such instructions
  in the smoke gram `.ll`, 0 in repro), and the dot becomes FOUR ymm accumulators of
  `vfmadd132pd` (16 lanes in flight; 44 packed FMAs in the full-size gram exe vs 0 in
  repro, which is a 13x-unrolled scalar `vfmadd132sd` chain). comoment3 / antisym likewise
  (16 / 42 packed FMAs licensed, 0 repro); antisym's two dots are fused into one loop with
  8 ymm accumulators.
- Harness note for M3: the LLVM `.ll` header comment contains the WORD "reassoc" in every
  build (`; appear ONLY on licensed accumulator chains (BLADE_FP_REASSOC)`), so the repro
  check must match the instruction flag (`fadd reassoc`), not the substring.

**Store audit, 24 full-size builds** (compile only; `cpp-serial`/`cpp-mt8` x
`repro`/`licensed`, `llvm-repro`/`llvm-licensed` with `BLADE_FP_CONTRACT=fast`). No LLVM
refusal in any build; mt8 builds import `libgomp-1.dll` and carry `main._omp_fn.0`, serial
builds neither. In EVERY build the production loop stores each cell to the pool; one
representative store per build:

| kernel | build | store (address: instruction) |
|---|---|---|
| gram | cpp-serial repro / licensed | `14000a646: vmovsd %xmm0,(%r11,%r9,8)` / `14000a8cd: vmovsd %xmm1,(%r10,%r8,8)` (after the `cmp $0x1f40` dot loop) |
| gram | cpp-mt8 repro / licensed | in `main._omp_fn.0`: `140001552: vmovsd %xmm0,(%r11,%r9,8)` / `140001983: vmovsd %xmm0,(%r11,%r8,8)` |
| gram | llvm repro / licensed | 64x64 brick tiles; `140001936: vmovsd %xmm0,(%r10,%r9,8)` / `140001a3e: vmovsd %xmm7,(%r10,%r9,8)` (+ edge-tile stores) |
| comoment3 | cpp-serial repro / licensed | `14000a99c: vmovsd %xmm0,(%rdi,%r10,8)` / `14000ac9f: vmovsd %xmm4,(%r14,%rbx,8)` (after `vdivsd`) |
| comoment3 | cpp-mt8 repro / licensed | `main._omp_fn.0.constprop.0`: `140001a9a` / `140001adf` |
| comoment3 | llvm repro / licensed | `140001842: vmovsd %xmm1,(%r14,%r15,8)` / `1400019cc: vmovsd %xmm4,(%r14,%r12,8)` |
| antisym | cpp-serial repro / licensed | `14000aa4d: vmovsd %xmm0,(%r14,%r11,8)` / `14000ad71: vmovsd %xmm2,(%r12,%r9,8)` (after `vsubsd`) |
| antisym | cpp-mt8 repro / licensed | `main._omp_fn.0`: `1400015cd` / `140001a74` |
| antisym | llvm repro / licensed | `14000181b: vmovsd %xmm0,0x57f6c0(%rax,%r12,8)` / `140001bf5: vmovsd %xmm2,0x57f6c0(%rax,%r11,8)` (after the fused 8-accumulator dot, `vfnmsub231sd`) |
| pairkernel | cpp-serial repro / licensed | `14000a785` / `14000a935: vmovsd %xmm11,-0x8(%r15)` (after `call exp`) |
| pairkernel | cpp-mt8 repro / licensed | `main._omp_fn.0`: `140001ad4: vmovsd %xmm10,(%rsi,%r12,8)` (both) |
| pairkernel | llvm repro / licensed | `140001831: vmovsd %xmm0,(%rbx,%r13,8)` / `14000185b: vmovsd %xmm0,0x0(%r13,%rdi,8)` |

(Section 12 re-does this audit on the post-flip binary; the addresses are unchanged.)

The section-10 notes on march (znver3 both compilers), `exp` (scalar UCRT import,
`vsqrtsd` inline, no vector math library), scalar unthreaded generation and M24 placement
hold unchanged for all 24 builds (the generator change adds two scalar rounds; still no
packed 64-bit multiply).

## 12. After the FP default flip (Blade.dll 74B67280..., bench merge 957afd95)

Knobs now: `licensed` = NO FP knob (the default honours every licence; the LLVM lane
contracts by default), `repro` = `BLADE_FP_REPRO=1`. My scripts pass nothing else: in
particular `BLADE_FP_CONTRACT` is no longer passed to the LLVM lane (dropped, as redundant);
`BLADE_FP_CONTRACT`, `BLADE_FP_REASSOC` and `BLADE_FP_REPRO` are cleared before every build.

**Validation** (`validate_A.py --lanes cpp,llvm --regime licensed|repro`): every number is
bit-for-bit the section-11 table (licensed = the old `BLADE_FP_REASSOC=1` results, repro =
the old knob-off results): worst 1.17e-13 licensed (cpp antisym, the near-zero cell), 6e-15
repro; exit 0 in both. The default build therefore computes exactly what the old licensed
build computed.

**Fold forms (smoke, serial; `foldform.ps1`).** `blade plan` now prints one `[fold-form v1]`
line per C++ fold site (it was silent before, F15):

| kernel | licensed (default) | repro |
|---|---|---|
| gram | `lambda in g: applied [fold form: simd-reduction; prodsum]` (the kernel dot) + 3 checksum/input sites `simd-reduction; reduce over a deferred computation (one level)` + 2 `K-lanes(8); reduce in expression position`; C++ `BLADE_OMP_SIMD_REDUCTION` x5, LLVM `fadd reassoc` x5 | every site `declined -- serial (repro) [fold form: serial (repro); ...]`; 0 SIMD_REDUCTION, 0 `fadd reassoc` |
| comoment3 | `lambda in t: simd-reduction; prodsum` + 4 deferred-fold sites + 2 K-lanes(8); SIMD_REDUCTION x6, reassoc x6 | all serial (repro); 0 / 0 |
| antisym | `lambda in l: simd-reduction; prodsum` (both dots) + deferred sites + 2 K-lanes(8); the input sum `__v114: applied [fold form: row-fold jam(6 rows, bitwise); reduce over the rows of a row map]`; SIMD_REDUCTION x4, reassoc x7 | all serial (repro) except the bitwise row-fold jam, applied in both; 0 / 0 |
| pairkernel | no kernel fold; 3 deferred checksum sites simd-reduction + 2 K-lanes(8); SIMD_REDUCTION x3, reassoc x4 | all serial (repro); 0 / 0 |

LLVM `contract` flags are present in BOTH regimes (repro contracts too; it only drops
reassociation). No LLVM refusal anywhere; smoke checksums unchanged.

**Store audit (24 full-size builds, compile only; `audit3`).**

| kernel | build | packed FMA (ymm) | scalar FMA | vaddpd ymm | SIMD_REDUCTION / `fadd reassoc` | omp_fn / libgomp | store (unchanged from section 11) |
|---|---|---|---|---|---|---|---|
| gram | cpp-serial repro / lic | 0 / 0 | 3 / 3 | 0 / 6 | 0 / 5 macros | 0 / 0 | `14000a646` / `14000a8cd` |
| gram | cpp-mt8 repro / lic | 0 / 0 | 3 / 3 | 0 / 6 | 0 / 5 | 1 / 1 | `140001552` / `140001983` (`main._omp_fn.0`) |
| gram | llvm repro / lic | 0 / 44 | 56 / 5 | 0 / 41 | 0 / 8 reassoc | - | `140001936` / `140001a3e` (64x64 bricks) |
| comoment3 | cpp-serial repro / lic | 0 / 0 | 3 / 3 | 0 / 6 | 0 / 6 | 0 / 0 | `14000a99c` / `14000ac9f` |
| comoment3 | cpp-mt8 repro / lic | 0 / 0 | 3 / 3 | 0 / 6 | 0 / 6 | 1 / 1 | `140001a9a` / `140001adf` |
| comoment3 | llvm repro / lic | 0 / 16 | 11 / 4 | 0 / 54 | 0 / 6 | - | `140001842` / `1400019cc` |
| antisym | cpp-serial repro / lic | 0 / 0 | 6 / 6 | 0 / 12 | 0 / 4 | 0 / 0 | `14000aa4d` / `14000ad71` |
| antisym | cpp-mt8 repro / lic | 0 / 0 | 6 / 6 | 0 / 12 | 0 / 4 | 1 / 1 | `1400015cd` / `140001a74` |
| antisym | llvm repro / lic | 0 / 42 | 23 / 2 | 0 / 230 | 0 / 7 | - | `14000181b` / `140001bf5` |
| pairkernel | cpp-serial repro / lic | 0 / 0 | 5 / 5 | 0 / 2 | 0 / 3 | 0 / 0 | `14000a785` / `14000a935` |
| pairkernel | cpp-mt8 repro / lic | 0 / 0 | 5 / 5 | 0 / 4 | 0 / 3 | 1 / 1 | `140001ad4` (both) |
| pairkernel | llvm repro / lic | 0 / 4 | 6 / 3 | 0 / 18 | 0 / 4 | - | `140001831` / `14000185b` |

Every production store survives in every build; addresses equal section 11's (one index
register differs in antisym cpp-serial licensed: `(%r12,%r10,8)` vs `(%r12,%r9,8)`).

**Differences versus section 11.** (1) Spelling only: the default build = the old
`BLADE_FP_REASSOC=1` build, bit-identical outputs, identical packed-FMA counts (gram 44,
comoment3 16, antisym 42, pairkernel 4 in LLVM licensed; 0 in C++ licensed, whose
simd-reduction dot is `vmulpd` + `vaddpd`), identical store addresses. (2) `blade plan` now
reports every fold site's form, so F15's "the plan is silent" no longer holds -- the
write-up can cite the plan. (3) antisym's input-sum row-fold jam (bitwise) is now KEPT
under the default; under the old `BLADE_FP_REASSOC=1` it was replaced by simd-reduction
accumulators (the plan lost its microkernel line then; now it reads `fold form: row-fold
jam(6 rows, bitwise)` in both regimes). That fold is the input checksum, not the kernel.
(4) LLVM `fadd reassoc` counts at full size (gram 8, comoment3 6, antisym 7, pairkernel 4)
vs the smoke `.ll` (5 for gram): more sites at full size, same mechanism; repro has 0
everywhere, so the harness's M3 check (match `fadd reassoc`, not the header word) passes
for repro.

## 13. Pilot diagnosis (serial-licensed, full size; session-231651)

Sources: the pilot's own executables and generated code
(`bench/results/2026-10-17-LAPTOP-350H6LDB/session-231651/build/<k>/full/serial-licensed-native/`),
disassembled with ucrt64 `objdump -d`; plus ATTRIBUTION runs I made at reduced size (below).
Pilot medians are the coordinator's (n = 3, uncapped). Nothing here is a benchmark result.

**Attribution runs (indicative, not results).** Each kernel at a reduced outer extent but its
FULL inner length, so the kernel : checksum work ratio equals the full-size one (kernel work
scales as p^2 n or p^3 n, the checksum as p^2 or p^3, both by the same power of p).
Variants: FULL (the committed source), NOCS (checksum replaced by one cell read; the output is
still materialized), and for pairkernel NOEXP (`exp(-a)` -> `1 / (1 + a)`). Serial, licensed
default, `BLADE_BLAS=0`, nine interleaved rounds, minimum shown (each run 1-15 ms):

| kernel (reduced size) | cpp FULL | cpp NOCS | llvm FULL | llvm NOCS | checksum share cpp / llvm |
|---|---|---|---|---|---|
| gram p=301, n=1001 | 9.22 | 8.67 | 5.70 | 5.62 | 6% / 1.4% |
| comoment3 p=91, n=201 | 14.99 | 4.46 | 5.97 | 3.82 | **70% / 36%** |
| antisym p=301, n=401 | 7.29 | 6.77 | 4.90 | 4.73 | 7% / 3.5% |
| pairkernel n=701 | 5.09 | 2.21 | 2.78 | 2.19 | **57% / 21%** |

pairkernel NOEXP: cpp 3.49 ms, llvm 1.14 ms, i.e. exp costs ~1.6 ms / 245,350 cells
= **6.6 ns per cell in both lanes** (59% of the LLVM FULL time, 75% of its kernel-only time).

**Achieved rates** (FMA lanes per second; peak of one Zen 3 core = 8 FMA lanes/cycle =
25.6-35.2 G/s at 3.2-4.4 GHz; a Blade C++-lane "lane" is a separate mul + add):

| kernel | work | blade-llvm | blade-cpp | expert tier (both compilers, range) |
|---|---|---|---|---|
| gram | 1,282,401 cells x 1001 = 1.284e9 FMA | 8.4 G/s (24-33% of peak) | 4.4 G/s (13-17%) | 16.3-18.2 G/s (46-71%) |
| antisym | 720,600 cells x 802 = 5.78e8 FMA | 7.7 G/s | 5.0 G/s | 16.7 (gf) - 18.6 (ifx) G/s |
| comoment3 | 848,046 cells x 201 = 1.705e8 terms | 4.0 G terms/s (kernel-only est. 6.8) | 1.6 (kernel-only est. ~5.3) | 14.7-15.4 G terms/s |
| pairkernel | 3,133,756 cells (1 exp each) | 13.4 ns/cell (kernel-only est. ~10) | 24.6 ns/cell | 9.8-10.4 ns/cell (naturals: 9.7-10.2 ns/cell over n^2 cells) |

### gram and antisym: the single-cell dot vs the 8x6 register tile (G1)

Expert inner loop (gfortran-expert.exe, `tile`, one sample s of an 8 x 6 tile):
```
vmovupd (%rdx),%ymm0 ; vmovupd 0x20(%rdx),%ymm14        # A panel: 8 doubles of x(i0..i0+7, s)
vbroadcastsd -0x30(%rax),%ymm2                          # one x(j, s), then 2 FMAs
vfmadd231pd %ymm0,%ymm2,%ymm13 ; vfmadd231pd %ymm14,%ymm2,%ymm8
... (6 broadcasts, 12 vfmadd231pd into 12 ymm accumulators) ...
```
= 12 ymm FMAs (48 lanes) per 8 loads: **0.67 loads per FMA, 2.3 bytes per FMA lane, 12
independent accumulators**, each cell still one chain in ascending s.

blade-llvm (`gram.exe`, the dot of one cell, 64x64 brick tiles around it):
```
vmovupd -0xe0(%r14,%r9,8),%ymm11 ...                    # 4 loads of row i
vfmadd132pd -0xe0(%r10,%r9,8),%ymm7,%ymm11 ...          # 4 FMAs, each with a row-j load
... x2 per iteration, 4 ymm accumulators, `cmp $0x3fc` (31 iterations of 32 lanes + tail)
vaddpd ; vaddpd ; vextractf128 ; vaddpd ; vshufpd        # per-cell horizontal reduction
```
= **2 loads per FMA, 16 bytes per FMA lane** (both operand rows re-read for every cell), 4
accumulators. Zen 3 sustains 2 ymm loads/cycle, so this loop is LOAD-bound at 1 ymm FMA per
cycle = half the FMA peak before any cache effect; the j row streams from L2 (a 64-row brick
of 8 KB rows is exactly the 512 KB L2). The expert's tile reuses each loaded A vector 6 times
and each broadcast 2 times. antisym's LLVM loop is the same shape with both dot products
fused into one loop (8 ymm accumulators, `vfnmsub231sd` at the end), still 2 loads per FMA.

blade-cpp (`gram.exe`, licensed):
```
14000a880: vmovupd (%rcx,%rax,1),%ymm1
           vmulpd  (%rdx,%rax,1),%ymm1,%ymm1
           add     $0x20,%rax
           vaddpd  %ymm0,%ymm1,%ymm0                     # the ONE accumulator
           cmp     $0x1f40,%rax ; jne 14000a880
```
**Not unrolled, one ymm accumulator, multiply and add not fused**: the loop-carried `vaddpd`
(3-cycle latency on Zen 3) limits it to 4 lanes per 3 cycles = 1.33 lanes/cycle, a third of
the LLVM loop's load bound. That is the C++-lane deficit on gram (attribution: 8.67 vs 5.62 ms
kernel-only, 1.54x; pilot 291 vs 153 ms, 1.90x) and antisym (two such single-accumulator dots
per cell; 6.77 vs 4.73 ms, 1.43x; pilot 1.54x). The `BLADE_OMP_SIMD_REDUCTION(+:__ps)` on the
prodsum is honoured by g++ as exactly one vector accumulator.

### comoment3: 2x the arithmetic, 4.5x the loads, and a rank-3 checksum tax

blade-llvm cell loop (4 accumulators, per 4 lanes): `vmovupd a ; vmulpd b ; vfmadd132pd c`
= 1 multiply + 1 FMA and 3 loads per term. The expert forms the weighted panel
W(i, s) = X(i, s) X(j, s) ONCE per (i panel, j) and runs the gram tile over k: 1 FMA per term,
0.67 loads per FMA. Operation count: Blade 2 x 1.705e8 = 3.41e8 vector-lane ops vs expert
1.705e8 FMA + C(p+1,2) n = 2.96e6 W multiplies = 1.73e8 -- **ratio 1.97**. Loads per term:
3 vs 0.67 (**4.5x**). The cell's inner length (n = 201) is short, so the per-cell
horizontal reduction and the 201 = 12 x 16 + 9 tail weigh more than in gram. Then the
checksum: 171^3 = 5.0M rank-3 canonicalizing reads = 36% of the LLVM time and 70% of the
C++ time (attribution); full-size split estimate for blade-llvm: kernel ~25 ms (2.3x the
expert's 11.1 ms) + checksum ~14-17 ms. In the C++ lane each read is `canon_fold<3>`
(inversion count + an out-of-line `std::sort`, `call ... __insertion_sort`) +
`canon_left_justify<3>` + a three-level nested-pointer load; the LLVM lane inlines the
compare-and-swap (no call): **4.9x cheaper per read** (10.5 vs 2.15 ms). That is most of the
C++ lane's 2.6x deficit here; its kernel is only 1.17x slower than the LLVM kernel.

### pairkernel: exp is the same in every arm; the 1.3x is the checksum

Both Blade lanes and gfortran-expert call the SAME scalar UCRT `exp`
(`__imp_exp` from `api-ms-win-crt-math-l1-1-0.dll`) once per cell; ifx links its own static
scalar `exp` (no SVML import, no packed exp call). sqrt is the inline `vsqrtsd` everywhere.
exp costs ~6.6 ns of the ~9 ns per cell (75% of the kernel). Blade's kernel-only cost per
cell (attribution NOCS: 8.9 ns llvm, 9.0 ns cpp) EQUALS the expert's whole per-cell cost
(9.8 ns incl. its fused checksum) and the natural's (9.7 ns, over all n^2 cells): the SoA
layout and the skipped diagonal (n of 3.1M cells) buy the expert nothing measurable. The
**1.36x gap is Blade's separate checksum pass**: n^2 = 6.27M canonicalizing reads over the
25 MB packed K (21% of LLVM time at n = 701; at full size the reads leave L2, pilot gap
42.0 - 30.8 = 11.2 ms = 1.8 ns per read), where the expert fuses the checksum per column from
L1 right after writing it. In the C++ lane the same read costs 4.9x more (57% of its time):
that is the C++ lane's whole 1.8x deficit on this kernel (kernels equal: 2.21 vs 2.19 ms).

### Gap-register lines (each: the emission change that closes it)

- **G1 (gram, antisym, comoment3 kernel)**: emit a same-array row-contraction under `comm` as
  a register-tiled outer product over the triangle (MR x NR = 8 x 6 tile, i-vectorized, the
  sample loop OUTSIDE the tile, packed panels, KC blocking), each cell one ascending FMA chain
  -- strict-legal, cuts loads from 2 to 0.67 per FMA (gram/antisym ~2x headroom measured
  against the expert).
- **G1b (comoment3)**: hoist the pair product of the outer comm levels out of the innermost
  level (form W = a*b once per (i, j), then FMA against c across k): halves the vector ops and
  feeds the same G1 tile.
- **G26 (new, C++ lane; gram, antisym)**: give `prodsum`'s licensed fold the K-lane form
  the plan already uses for expression-position reduces (`K-lanes(8)`: 4-8 independent ymm
  accumulators, mul+add contracted) instead of one `omp simd reduction` vector accumulator --
  closes the C++-vs-LLVM 1.4-1.9x on the dot kernels.
- **G3a (all four, both lanes; comoment3 and pairkernel worst)**: the checksum tax is now
  measured, not estimated: 36% (comoment3) / 21% (pairkernel) of the LLVM time; closing it
  needs G2/G3's canonical-cell fold (read the pool once with orbit weights 1/2 or 1/3/6),
  or fusion of the fold into the producing nest.
- **G3b (C++ lane)**: inline the canonicalizing read (`canon_fold<R>` + `canon_left_justify`:
  replace `std::sort` + inversion count with an inline sorting network / min-max, and read the
  flat pool instead of chasing nested row pointers) -- 4.9x per read vs the LLVM lane; it is
  the C++ lane's whole deficit on pairkernel and most of it on comoment3.
- **G11 note**: none of the above involves threads; all numbers are serial.

## 14. mt8 threading of preparation and checksum (source change approved 2026-10-18)

Before this change only the kernel's level 0 carried a thread licence; the input generation,
the checksum nest and the input sum ran serial in the mt8 build, while the expert Fortran
threads all of them. Change, in all four kernels (and mirrored in the four validation dumps):

- input map: `lambda(i, s) where omp(i: 1) -> lcg(...)` (antisym `lambda(i, c, s)`,
  pairkernel `lambda(i, d)`), the same depth-1 spelling as the kernel's `omp(a: 1)`;
- checksum and input sum: the OUTER fold kernel carries the licence,
  `reduce(<row map>, lambda(u, v) where omp -> u + v, 0.0)`;
- antisym only: the input sum is bound to its own `let isum` (in expression position the
  licensed fold over the materialized row-sum array "opens no team").

Spelling note (finding): putting `where omp(i: 1)` on the checksum's ROW MAP instead
(`reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> ..., (+))`) is accepted but
the licence is DROPPED with no plan or census line in the mt8 plan -- the fold kernel is
the only placement that threads (blade-B's earlier observation, G14; the silence is a G11/G24
style observability gap).

**mt8 plan (C++ lane, full-size sources)** -- every kernel now shows three thread sites:

| kernel | input map | kernel (unchanged) | checksum fold | input-sum fold |
|---|---|---|---|---|
| gram | `__v21: BLADE_OMP_PARALLEL_FOR; level 0` | `__v27: ..._DYNAMIC; level 0` | `__v54: omp parallel num_threads(T) + fixed-order combine; ... Path B` (fold form `threaded(Path B chunks, serial within each chunk)`) | `__v81: Path B` |
| comoment3 | `__v21: PARALLEL_FOR` | `__v29: _DYNAMIC` | `__v88: Path B` | `__v119: Path B` |
| antisym | `__v26: PARALLEL_FOR` | `__v34: _DYNAMIC` | `__v61: Path B` | `__v94: BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION(+:__v94) ... (Path A)` |
| pairkernel | `__v24: PARALLEL_FOR` | `__v33: _DYNAMIC` | `__v60: Path B` | `__v87: Path B` |

(The Path B chunk interiors are serial chains -- G25 -- so the threaded checksum is threads
without lanes; in the serial build the same folds are simd reductions.)

**Verification (tiny sizes only).** `blade check` clean at full size (all four). Smoke size
(pins from `pins-A.json`), C++ default build with `OMP_NUM_THREADS=2`, C++ serial
(`BLADE_OMP_THREADS=1`) and LLVM: identical printed values in all three arms, no LLVM
refusal:

| kernel | checksum (all arms) | rel. to pin | input_checksum (all arms) | rel. to pin |
|---|---|---|---|---|
| gram | 237924.717413045 | 1.3e-15 | 952.456307353452 | 2.1e-16 |
| comoment3 | 45.0076154756788 | 6.7e-16 | 71.5238295937888 | 4.2e-16 |
| antisym | 390.607241954913 | 9.2e-16 | 239.513063936029 | 1.5e-15 |
| pairkernel | 1051.80125396941 | 8.6e-16 | 73.6780804120935 | 6.8e-16 |

All checksums within 1e-12 of the pins. Every input_checksum equals the pin to 15 digits
except pairkernel's: Blade prints 73.6780804120935 and `%.15g` of the fsum pin is
73.6780804120936 (pin 73.67808041209355, a half-way case at the 15th digit; Blade's binary sum
is 6.8e-16 below). That was already the case before this change (section 11); the harness
should compare input_checksum with a relative tolerance (~1e-14), not by 15-digit text.
`validate_A.py --lanes cpp,llvm` with `OMP_NUM_THREADS=2`: licensed worst 1.17e-13, repro
worst 6e-15, exit 0 in both -- identical to section 12.

**Serial emission before vs after** (`blade emit` with `BLADE_OMP_THREADS=1`, full size, same
source path; normalized for generated-identifier renumbering, the fold lambdas' parameter
names and the `[omp] requested but emitted serial` census comments; `scratchpad/normdiff.py`):

- gram, comoment3, pairkernel: **identical** (the only remaining line differs by an
  identifier number the normalizer does not rewrite). Their serial arithmetic, fold forms and
  loop nests are unchanged; the existing serial results stand.
- antisym: **differs beyond pragma/comment lines, in the input sum only.** The final
  1201-term fold of the input checksum changes from the K-lanes(8) form seeded with its first
  element (`__r = v[0]; __rlo = 1; ...8 lanes...`) to a flat `BLADE_OMP_SIMD_REDUCTION(+:...)`
  loop seeded with 0.0 (the Path A form a `let`-bound licensed fold takes in a serial build),
  and two `BLADE_FRAME` names change (`lambda in isum`). Kernel, checksum and input generation
  are unchanged. Effect: the association of a 1201-term sum (input_checksum identical at 15
  digits at smoke size) and ~1 microsecond of work in a ~75 ms run. By the rule given
  ("differs beyond pragma/macro lines"), the serial-licensed antisym cells should be re-run on
  the new source; gram, comoment3 and pairkernel need no re-run.
