# LLVM-lane acceptance probes, group B + controls (blade-B agent)

Binary: `bin\Release\net10.0\Blade.exe` in this worktree, 2026-10-17. Every probe was run in
both lanes (`BLADE_BLAS=0`; `BLADE_LLVM=1` for the LLVM lane) at tiny extents (n <= 1009,
grids <= 7 x 11; every run under 0.1 ms of program time). Probe sources live in the agent's
scratch dir (`<scratchpad>/p/*.blade`); minimal reproducers for the findings are reproduced
inline below so they survive the scratch dir. "Nests" = loop nests over the data counted in
the emitted C++ (`blade emit`) and loop headers per function in the LLVM lane's `.ll` (from
`blade run --verbose`, which keeps the build dir). Every checksum below was cross-checked
against a Python reference (left-fold sums in storage order), so all agree to at least 15
digits.

## 0. Findings that change the suite (read these first)

F0. **The charter's original input generator was an affine sawtooth.** Two affine LCG
    rounds compose into one affine map, so u(k) = frac(alpha k + beta): lag-1 autocorrelation
    -0.50, lag-2 +0.76, every second difference an integer. A 5-point Laplacian checksum came
    out exactly 114.0 in both Blade and Python. A first fix (R3: affine, quadratic, affine)
    still had long-lag correlations, non-geometric runs and higher-order neighbour dependence
    (`bench/notes/generator-check.md`). It was REPLACED on 2026-10-17 by variant D (R5:
    affine, quadratic, affine, middle-square, affine), now README section 5. Reference values
    under D (seed 10000019), reproduced in BOTH lanes and in numpy: final states h5 =
    408748897, 189020261, 1945512958 for k = 0, 1, 2. For x(k) = gen(k, 10000019, -1, 1),
    k = 0..9: sum = -3.1010268041864, sum of squares = 3.4272198544854. Every kernel, twin
    and dump in this file's scope uses D.

F1. **The LLVM lane ignores `--print` / `BLADE_PRINT`.** `src/EmitLlvm.fs` never consults
    `CodeGenState.printSelection`; it prints every top-level binding. Minimal reproducer:

    ```
    let x = [1.0, 2.0, 3.0]
    let y = 4.0
    let s = reduce(x, (+))
    ```
    `BLADE_LLVM=1 blade run f.blade --print s` prints `x = [...]`, `y = 4.0`, `s = 6.0`; the
    C++ lane prints only `s = 6.0`. (A name that is not a binding still refuses with BL7004
    in BOTH lanes, because that check runs in the shared front end.)

F2. **The LLVM lane has NO OpenMP at all.** The `.ll` has no `__kmpc_*`/`omp` symbol, clang
    is invoked without `-fopenmp`, and nothing is refused or noted: a `where omp` licence is
    silently compiled serial. **Consequence for the harness: the `blade-llvm` arm in `mt8` is
    a SERIAL program.** Report it as "llvm lane: no thread form" (or do not run it), never as
    an 8-thread measurement. The mt8 Blade figure is `blade-cpp`.

F3. **The C++ lane ALWAYS compiles with `-fopenmp`** (`Build.fs` line ~1001; `ompFlag` is
    unconditional), including under `BLADE_OMP_THREADS=1`. The knob only stops THREAD
    pragmas from being emitted; every `BLADE_OMP_SIMD` / `BLADE_OMP_PARALLEL_FOR_SIMD` /
    `BLADE_OMP_SIMD_REDUCTION` macro that is emitted is LIVE in the serial arm, and libgomp
    is linked. (The charter's "+ `-fopenmp` when pragmas are emitted" is inaccurate.) For
    README section 2's "vectorization hints" item: Blade's serial build DOES keep its `omp
    simd` hints active. The one strict-regime hazard: a knob-suppressed "Path A" fold (bare
    `where omp` on a builtin `+`/`*` fold at TOP LEVEL) is re-emitted as
    `BLADE_OMP_SIMD_REDUCTION`, which reassociates in the serial arm. No kernel of mine uses
    that shape (all folds live in functions, Path B or plain serial); each kernel's serial
    emit is grepped for `SIMD_REDUCTION` below.

F4. **The reduction join drops `omp` SILENTLY.** Infix chain
    `reduce((l <@> k1) <&!> (l <@> k2), lambda(a, b) where omp -> a + b, 0.0)` and pack join
    `object_for(<&!>) <@> (reduce(x, lambda(a, b) where omp -> a + b, 0.0), ...)`, both bound
    to their own `let` in a function body at n = 1000003: no pragma, no `[omp] requested but
    emitted serial` note in the C++, and nothing in `blade plan`. That breaks the census
    invariant that a dropped omp licence is never silent. (`omp` on the MAP legs,
    `l <@> lambda(v) where omp -> ...`, does print the note "fold accumulates into a shared
    scalar, which is not race-safe".) **There is no licensed parallel spelling of a `<&!>`
    join.** The only parallel one-pass multi-statistic fold is the tuple-state fold
    (`reduce(method_for(x) <@> lambda(v) -> (v, v * v), lambda(a, b) where comm(a, b), omp ->
    (a[0] + b[0], a[1] + b[1]), (0.0, 0.0))`, Path B chunked in C++), and the LLVM lane
    REFUSES that ("a fused reduction whose kernel/seed pack does not match its leaf count").

F5. **C++ ICE (BL9002) for a deferred map consumed in a TOP-LEVEL BLOCK.** Minimal
    reproducer (the LLVM lane runs it correctly, 3.5; inside a function the C++ lane is fine
    too):

    ```
    function sq(v: Float64) -> Float64 = { v * v }
    function half(v: Float64) -> Float64 = { 0.5 * v }
    let s = {
        let x = [1.0, 2.0, 3.0]
        let r = method_for(x) <@> half
        reduce(method_for(r) <@> sq, (+))
    }
    ```
    g++: `'__v11' was not declared in this scope`: the deferred `r` is commented out
    (`// __v11 = <deferred computation>`) and then read as an array.

F6. **The LLVM lane refuses a function call in tuple-destructuring position.**
    `let (checksum, input_checksum) = run()`, where `run()` returns a tuple, gives "the IR
    node IRApp in pack position -- the llvm lane builds packs from tuples, <&>/<&!> legs and
    reduction joins only". The working contract shape, in both lanes, is a top-level BLOCK
    whose tail is a tuple literal of function calls (functions hold every array, so F5 does
    not bite):

    ```
    let (checksum, input_checksum) = {
        let x = inputs()
        (kernel(x), reduce(x, (+)))
    }
    ```
    The block's local array `x` is not printed in either lane; the clock encloses the block
    in both lanes (C++: `__blade_t0` is the first statement of main and `__blade_t1` follows
    the tuple destructure; `.ll`: `blade_now()` before `bl_inputs()` and after the last fold,
    then `blade_print_completed`).

F7. **`compound(dense, mask)` is refused by the LLVM lane** ("a compound-index
    initializer"), in addition to being top-level-only (BL6002). So `maskagg` has NO LLVM
    arm: the `blade-llvm` build always falls back. Because it falls back, F1 cannot echo the
    top-level arrays either: the fallback is the C++ lane, which honours `--print`.
    Measurable Blade arm: `blade-cpp` only.

F8. **Lifted lets are materialized, one temporary per `let`.** Elementwise fusion splices
    maps WITHIN one binding's expression, never across a `let`, even a single-use one:
    `let y = exp(0.75 * x + 0.25) / (1.0 + x * x)` then `reduce(y * y, (+))` is two nests
    with an n-cell temporary; three stage lets are three temporaries plus the fold (with pool
    reuse, 3 -> 2 pools). Same in both lanes. (README 7.5 says an unforced lifted chain is
    fused into the fold and never stored; in these probes it is always stored. Forcing named
    outputs with `|> compute` is right either way.) Deferred `method_for(x) <@> f` stages
    bound by `let` inside a function are ALSO each materialized (no fusion decision at all
    in `blade plan`). What fuses into ONE loop with the fold, in both lanes: (i) a composed
    stage object `object_for(f) >>@ object_for(g) >>@ ...` bound at TOP LEVEL and applied
    inside a function, `reduce(chain <@> a, (+))`; (ii) a single block kernel,
    `reduce(method_for(a) <@> lambda(v) -> {...}, (+))`; (iii) an inline lifted expression
    as the fold's operand, `reduce(abs(exp(...) / (...)), (+))`.

F9. **Fold licence placement (C++ lane, n = 1000003, emit only):**

    | spelling (inside a function) | emitted |
    |---|---|
    | `reduce(x, lambda(a, b) where omp -> a + b)` as the function's TAIL expression | serial + note "reduce in expression position opens no team ... bind the fold to its own let" |
    | `let s = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)` | parallel, Path B (`BLADE_OMP_PARALLEL_NUM_THREADS`, contiguous chunks, fixed-order combine) |
    | `reduce(x * x, lambda(a, b) where omp -> a + b, 0.0)` (lifted operand) | parallel, Path B, "reduce over computation ... outer level chunked" |
    | `let s = reduce(method_for(zip(a, b)) <@> lambda(x, y) where omp(x: 1) -> x * y, (+))` (licence on the MAP) | SERIAL + note "fold accumulates into a shared scalar" (README 7.4 says this spelling "streams": it streams, but it does not thread) |
    | `method_for(range<I>) <@> lambda(i) where omp -> ... \|> compute` (map) | `BLADE_OMP_PARALLEL_FOR` |
    | `method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> ... \|> compute` | `BLADE_OMP_PARALLEL_FOR_SIMD` |
    | `method_for(range<halo<Y,..>, halo<X,..>>) <@> lambda(wr, wc) where omp(wr: 1) -> ...` | `BLADE_OMP_PARALLEL_FOR` on the row loop |
    | `reduce(x, lambda(a, b) where comm(a, b), omp -> if a > b then a else b)` (tail) | serial + the expression-position note |

F10. **Whole-rank fold `reduce(m, (+), axes = 2)` is ACCEPTED by the LLVM lane on a
    `halo`-map operand** (`c1c.blade`; no refusal, same checksum). The fixture's refusal
    note is about an outer-product operand; I did not re-probe that shape.

## 1. Acceptance table

| idiom (probe file) | C++ lane | LLVM lane | nests (C++ / .ll) | notes |
|---|---|---|---|---|
| (a-i) lifted chain, 2 lets e, r -> `reduce(r * r, (+))` (a1) | ok 327.149777090351 | ok, same | 3 + input / 5 in main | e and r each materialized (F8) |
| (a-i) one lifted let -> `reduce(y * y)` (a1b) | ok 1364.46233501178 | ok, same | 2 / 2 | temp of n cells; the `y * y` fuses into the fold |
| (a-i) lifted inline in the fold operand (a1d) | ok 1098.96503564619 | ok, same | 1 / 1 | fully fused |
| (a-ii) 3 deferred `method_for` stage lets in a function (a2) | ok 327.149777090351 | ok, same | 4 / 4 | every stage materialized (pool reuse 3 -> 2) |
| (a-ii) same in a top-level block | **ICE BL9002** (F5) | ok | - | reproducer F5 |
| (a-ii) one block kernel `reduce(method_for(x) <@> stage, (+))` (a2b) | ok 327.149777090351 | ok, same | 1 / 1 | fused |
| (a-iii) `object_for >>@` x4 at top level, applied in a top-level block (a3) or in a function (fc1) | ok | ok, same | 1 / 1 | fused by substitution; subexpressions are duplicated textually (exp 4x in the `.ll` source) but clang -O3 CSEs them to ONE `callq exp` (checked in the assembly), and g++ sees `blade_libm_exp` as `__attribute__((const))` |
| (b) infix `<&!>` join, 5 legs, all `(+)` (b1) | ok 1550.47632377995 | ok, same | 1 / 1 | 5 accumulators in one loop |
| (b) pack join `object_for(<&!>) <@> (reduce(x,(+)), reduce(x*x,(+)), reduce(x, max, -1e300), reduce(x, min, 1e300))` (b2) | ok 340.333711613556 | ok, same | 1 / 1 | MIXED fold operators in one traversal; a lambda leg needs an explicit seed (BL3999 otherwise) |
| (b) tuple-state fold `(v, v*v, v)` with a block merge (b3) | ok 339.336580303153 | **refused**: "a fused reduction whose kernel/seed pack does not match its leaf count" | 1 / - | the only parallel one-pass form (F4) |
| (b) top-level tuple destructuring of a call `let (c, i) = run()` | ok | **refused**: "IRApp in pack position" (F6) | - | use the top-level block form |
| (c) 2-D 5-point via `range<halo<Y,[-1,0,1]>, halo<X,[-1,0,1]>>`, `reduce(reduce(lap * lap, (+)), (+))` (c1) | ok 120.213917123424 | ok, same | 4 (stencil, square, row-fold jam, final) / - | NO edge guards: interior loop bounds plus one BL8009 extent check per call, outside the loop |
| (c) same, squared inside the kernel (c1b) / `axes = 2` (c1c) | ok, same | ok, same | 2 / 2 nests | the rank-2 halo map is materialized before the fold (one temp) |
| (c) 1-D 5-point `halo<I, [-2,-1,0,1,2]>`, map then `reduce(d * d)` (c2) | ok 15735.0604200273 | ok, same | 2 / - | no guards |
| (c) 1-D 5-point, squared in the kernel, `reduce(method_for(halo<...>) <@> ..., (+))` (c2b) | ok, same | ok, same | 1 / 1 | fully fused |
| (d) 3-arm `let rec` in a function, nonlinear one-lag (d1) | ok 38.5306818003401 | ok, same | zero-fill + recurrence + fold / 2 | at small N the zero history is a `static const` literal; at N >= ~1e5 a zero-fill loop (a dead store: every cell is overwritten) |
| (d) 3-arm `let rec`, two-term lag `prefix(n - 2)` with implicit zero history (d2) | ok 4.20488032251902 | ok, same | same | no `IRForRange` refusal for either shape |
| (e) `mask` + `compound` + `reduce` (e2, top level only) | ok 109.652530161602 | **refused**: "a compound-index initializer" (F7) | - | BL6002 inside a function |
| (e) conditional kernel `reduce(method_for(zip(x, q)) <@> lambda(a, b) -> if b < 0.3 then a * a else 0.0, (+))` (e3) | ok 109.652530161602 | ok, same | 1 / 1 | ternary, branch-free candidate |
| (f) dot `reduce(x * y, (+))` / `prodsum(x, y)` (f1) | ok | ok, same | 1 / 1 each | `blade plan` sees `[blas-routing] ... [Dot (L1, d)]`, declined under BLADE_BLAS=0. In the `blas` regime `reduce(z * z, (+))` and dot shapes WOULD route to BLAS; the suite runs blas for gram only |
| (f) saxpy `let z = 2.5 * x + y` then `reduce(z * z, (+))` (f1) | ok (f1 total 4105.50083170063) | ok, same | 2 / 2 | z materialized (F8) |
| (g) LCG inputs: `method_for(range<Ix>) <@> lambda(i) -> gen(Int64(i), seed, lo, hi) \|> compute` | ok | ok | 1 / 1 | `gen(0..n, ...)` (lifting a scalar user function over a range) is BL3001; lifting the Int64 arithmetic inline over `0..n` works in both lanes but is less readable; a parametrized `inputs(seed, lo, hi)` function works in both lanes |

Timing line: in every probe the C++ lane's `__blade_t0 = TIME` is the first statement of
`main` and `__blade_t1` follows the last top-level binding; the `.ll` brackets the same
region with `blade_now()`, and the prints come after `blade_print_completed`.

## 2. Idiomatic spellings that work in both lanes (for the kernels)

- Contract shape: functions hold every array; top level is the block of F6.
- Generator (README section 5, variant D), exact in Int64. `1103515245 * (k + seed)` stays
  below 2^56 for k + seed < 2^25, and h3 * h3 < 2^62:

  ```
  function gen(k: Int64, seed: Int64, lo: Float64, hi: Float64) -> Float64 = {
      let h1 = ((k + seed) * 1103515245 + 12345) % 2147483648
      let h2 = (h1 * (h1 / 4096 + 1)) % 2147483648
      let h3 = (h2 * 1103515245 + 12345) % 2147483648
      let h4 = (h3 * h3 / 65536) % 2147483648
      let h5 = (h4 * 1103515245 + 12345) % 2147483648
      lo + (hi - lo) * (Float64(h5) / 2147483648.0)
  }
  ```
- Fused chain: a top-level composed stage object, applied inside the function (F8).
- One pass: the `<&!>` join (serial; no parallel form, F4).
- Stencil: the product halo, with the square inside the kernel.
- Recurrence: the 3-arm `let rec` inside a function.
- Filter: top-level `mask`/`compound` (C++ only) or the conditional kernel (both lanes).

## 3. Kernels written (Task 2)

Every kernel source and every `inputonly.blade` twin (fusedchain, onepass, stencil, maskagg,
dot, saxpy) is committed at FULL size with `// SIZE` markers. Each was verified at the
spec's SMOKE size, in a scratch copy with the marked literal rewritten:

- `blade check` clean;
- C++ lane serial (`BLADE_OMP_THREADS=1`), C++ lane threaded (knob unset,
  `OMP_NUM_THREADS=2`) and LLVM lane (`BLADE_LLVM=1`) all run with
  `--print checksum,input_checksum`;
- no fallback line, except maskagg (expected, F7);
- serial == LLVM to all 15 printed digits; the threaded C++ build differs only in the
  last digit, where a chunked fold is in play;
- a Python reference (left folds in storage order, `<scratchpad>/refk.py`) agrees to 15
  digits.

Full-size `blade emit` (serial, and serial + `BLADE_FP_REASSOC=1`) succeeds for every file:
11-34 KB of C++ (no giant literals), zero `SIMD_REDUCTION` sites in any serial build. The
only `omp simd` in a serial build is saxpy's map loop (`BLADE_OMP_SIMD`, not a reduction).

### Reference values for the Fortran agent (generator D, smoke sizes; Blade prints 15 significant digits)

The authoritative pins, smoke AND full, are `bench/validation/pins-B.json`: the numpy
references `bench/validation/ref_<kernel>.py`, exactly rounded sums. The values below were
re-verified after the switch to D: Blade serial C++ and LLVM agree with the pins to all 15
printed digits, and the 2-thread C++ build differs by at most 1 in the last digit (saxpy).

| kernel | smoke size | checksum (numpy pin) | input_checksum (numpy pin) | Blade (both lanes) |
|---|---|---|---|---|
| fusedchain | n=20011 | 46.304649681954004 | 10027.016977054533 | 46.304649681954 / 10027.0169770545 |
| onepass | n=50021 | 1122911.4825406745 | 50086.33482095506 | 1122911.48254068 / 50086.3348209551 |
| stencil | ny=31, nx=41 | 1832.4204836448714 | 636.53345707757398 | 1832.42048364487 / 636.533457077574 |
| recur | nt=11, nx=211 | 2729.7354831553357 | 211.4869475774467 | 2729.73548315534 / 211.486947577447 |
| maskagg | n=30011 | 50009.275003391587 | 30049.359229410999 | 50009.2750033916 / 30049.359229411 (cpp; llvm = fallback) |
| dot | n=30011 | 7442.4875710150263 | 29915.989193804096 | 7442.48757101503 / 29915.9891938041 |
| saxpy | n=20011 | 36580.347055559563 | 19956.494551840704 | 36580.3470555598 / 19956.4945518407 |

maskagg at smoke size keeps m = 15012 of 30011 cells under D.

### Per kernel

**fusedchain** (`fusedchain.blade`). The three stages and the square are top-level
`lambda` values composed `object_for(affine) >>@ object_for(squash) >>@ object_for(bend)
>>@ object_for(square)`; the kernel is `let s = reduce(chain <@> a, lambda(p, q) where
omp -> p + q, 0.0)`.
- Emitted shape, both lanes: ONE loop over `a`, no n-sized temporary. In C++ the kernel
  becomes a `std::function` local in `main`, because it captures the top-level chain; that
  is one call, negligible.
- The composition is inlined by substitution, so `s1 / (1 + |s1|)` appears textually
  several times in the loop body; the back ends CSE it (pure arithmetic).
- mt8: input map `BLADE_OMP_PARALLEL_FOR`; fold Path B (`BLADE_OMP_PARALLEL_NUM_THREADS`,
  per-thread contiguous chunks, fixed-order combine).
- fast: C++ lane, no lanes on the kernel fold (F11); LLVM lane, `fadd reassoc nsz`.

**onepass** (`onepass.blade`). `<&!>` join of four `(+)` legs over `l = method_for(x)`.
- Emitted shape: ONE loop, four accumulators, powers spelled `((x*x)*x)*x` in the emitted
  C++.
- mt8: input map threaded; the join has no parallel form (F4). Report "no parallel form".
- fast: C++ lane, unchanged (F11); LLVM lane, the legs get `reassoc`.

**stencil** (`stencil.blade`).
- lap: `method_for(range<halo<Y,[-1,0,1]>, halo<X,[-1,0,1]>>) <@> lambda(wr, wc) where
  omp(wr: 1) -> ... |> compute`, stored (named output).
- checksum: `let s = reduce(method_for(lap) <@> lambda(row) -> prodsum(row, row),
  lambda(p, q) where omp -> p + q, 0.0)`.
- `blade plan`: `[omp v1] applied [BLADE_OMP_PARALLEL_FOR; loop nest level 0]` for inputs
  and lap, plus `[omp v1] ... Path B` for the checksum.
- No edge branches: interior bounds, and one BL8009 extent check per call outside the nest.
- **Finding (the spec asked):** the spec's `reduce(reduce(lap * lap, (+)), (+))`
  MATERIALIZES a second (ny-2) x (nx-2) array for `lap * lap`; so does
  `reduce(lap * lap, (+), axes = 2)`. A `where omp` fold kernel on the `axes = 2` form is
  dropped silently (no pragma, no note). At full size that temporary would also push the
  working set to about 72 MB, over the cap. The per-row prodsum spelling streams, threads,
  and is the same rung as README 7.2's per-row checksum nest.
- The row width is derived as `Int64(extents(range<X>))`, so `nx` is not written as a
  second literal (README section 4).
- fast: the per-row prodsum gets `BLADE_OMP_SIMD_REDUCTION` (C++).

**recur** (`recur.blade`). The spec's idiom verbatim: `rhs`, `step` (four stage lets), and
the 3-arm `let rec` over `T` inside `kernel`.
- Checksum: `reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), (+))`. The
  alternative `reduce(reduce(traj * traj, (+)), (+))` would materialize a second 48 MB
  trajectory (over the cap).
- Emitted shape: per step, about eight row-sized allocate/free pairs (each stage expression
  materialized), with fused `__fp_cells` loops; then a copy into the trajectory row.
  Zero-fill pass over the trajectory before the recursion.
- LLVM lane: accepted (no `IRForRange` refusal).
- Serial only; the source carries no licence.

**maskagg** (`maskagg.blade`). Top-level `x` (`|> compute`), `keep = mask(x, lambda(v) ->
v > 1.0)`, `y = compound(x, keep)`, `checksum = reduce(y * y, (+)) + Float64(extents(y))`.
- LLVM lane: refused ("a compound-index initializer"), falls back to C++, which honours
  `--print`, so no echo. Measurable Blade arm: blade-cpp.
- Emitted shape: Bool mask pass; a `std::vector<bool>` copy of the mask; a
  `compound_index_t` build that ENUMERATES the mask and inserts every kept cell into a
  `std::unordered_map` (`src/cpp/index_types.h:253-317`); a branchless gather; a `y * y`
  temporary; a fold.
- **Finding / safety:** measured at n = 300007 (1/10 of full): 16-30 ms, peak working set
  16-17 MB (x alone is 2.4 MB). By linear extrapolation (an estimate, not a measurement)
  about 120 MB and 200-300 ms at the original full n = 3,000,017, over the 64 MB cap.
  DECIDED: full n is cut to 1,000,003 (the SIZE lines of maskagg.blade and its twin;
  estimate about 40 MB peak). The hashed rank-1 compound index is in the gap register. The twin keeps x inside the block instead (no compound
  is needed there), so it is also safe in the LLVM lane.

**dot** (`dot.blade`). `let s = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y,
lambda(p, q) where omp -> p + q, 0.0)`.
- Emitted shape: streams (no `a * b` temporary); mt8 uses Path B.
- `blade plan` notes the dot shape for BLAS routing (declined under `BLADE_BLAS=0`).
- fast: C++ lane, no lanes (F11); LLVM lane, `fadd reassoc nsz` (the checksum changed in
  the last digit under REASSOC, so the reassociation is real).

**saxpy** (`saxpy.blade`). `let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1)
-> 1.5 * u + v |> compute` (the licensed co-iteration, because the lifted `1.5 * x + y` has
no licence slot), then `let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q)
where omp -> p + q, 0.0)`.
- Emitted shape: y2 allocated and written in one loop (`BLADE_OMP_PARALLEL_FOR_SIMD` in
  mt8, `BLADE_OMP_SIMD` in serial), then a streaming checksum fold (Path B in mt8). No
  y2-squared temporary.

**Every kernel's contract shape:**

```
let (checksum, input_checksum) = { let x = inputs(); (kernel(x), reduce(x, (+))) }
```
The input map carries `where omp` (or `omp(i: 1)` for the grid) in the kernels whose spec
has an mt8 regime; recur and maskagg carry no licence. `input_checksum` is a plain serial
`reduce(x, (+))` in every regime.

F11. **`BLADE_FP_REASSOC=1` in the C++ lane does not reassociate folds over a deferred
    computation.** Covered: a composed chain, a `<&!>` join, a zip map, and a map over a
    stored array. Only plain-array folds (`reduce(x, (+))`, 8 lanes) and `prodsum` (`omp simd
    reduction`) change. The LLVM lane does mark those folds `fadd reassoc nsz`. In the fast
    regime, blade-llvm is therefore the meaningful Blade arm for fusedchain, onepass, dot and
    saxpy, and blade-cpp-fast equals blade-cpp-strict there.

## 4. Round 2: methodology-review items M6, M8, M10, M11, M12, M24 (2026-10-17)

All Python here runs as `C:\Python314\python.exe -P <script>`. Do NOT use `-I`: this host's
NumPy is in the user site-packages, which `-I` hides. The scripts add their own directory
to `sys.path`.

**Reusable tooling** lives in `bench/validation/tools/`. The paths are repo-relative and
none of them runs a full-size program.
- `smoke_B.sh <outdir> [k ...]`: smoke runs of each kernel + twin; check, C++ serial,
  C++ 2-thread, LLVM.
- `foldform_B.sh <outdir>`: fold-form evidence, `.cpp` SIMD_REDUCTION / lane sites and
  `.ll` `fadd reassoc`, under both FP settings.
- `audit_B.sh <licensed|repro> <outdir> [k ...]`: full-size compile-only builds in both
  lanes, `objdump -d`, loop summaries.
- `plan_B.sh`: the `blade plan` lines.
- `asmfn.py <file.s> <function-substring>`: per-loop stores, FMAs, vpmul and calls.

Re-run all of these after the compiler's FP-default flip. The two FP settings are named in
one line at the top of `foldform_B.sh` and `audit_B.sh`; switch them to unset /
`BLADE_FP_REPRO=1` then. The scratch-dir paths below refer to the first run of the same
scripts.

### M6: generator check -> `bench/notes/generator-check.md`

`bench/validation/generator_check.py` runs at the specs' FULL sizes, with closed forms and
numpy PCG64 as the iid yardstick.
- The R3 formula had measurable defects (before/after table at the top of
  generator-check.md):
  - long-lag correlation |r| up to 0.27;
  - 570 correlated gram row pairs;
  - maskagg run lengths off by up to 36%;
  - stencil-lap kurtosis -0.742 instead of -0.780;
  - low-bit bias.
- The coordinator adopted variant D = R5. Under D every check reads OK (9 verdicts) and
  every defect metric sits at PCG64's level.
- `bench/validation/benchgen.py` defaults to R5. R3 is reachable only with
  `BENCH_GENERATOR=R3` or `variant="R3"`, for the before/after column.

### M11/M12: references, pins, dumps, validation

- **References.** `bench/validation/ref_{fusedchain,onepass,stencil,recur,maskagg,dot,saxpy}.py`
  are independent numpy transcriptions of each spec.
  - Sums are exactly rounded (`math.fsum` over the float64 terms), so the pins do not depend
    on any summation order.
  - Elementwise math is plain IEEE without FMA. Blade's lanes contract FMAs, so expect
    agreement to about 1e-15 relative, not bit equality.
- **Pins.** `bench/validation/make_pins_B.py` writes `bench/validation/pins-B.json`:
  {kernel: {smoke|full: {checksum, input_checksum}}}, 17 significant digits, variant D.
  Smoke pins equal Blade's printed values to 15 digits for all 7 kernels in both lanes
  (section 3 table).
- **Dumps.** `bench/validation/dump/{stencil,recur,maskagg}_dump.blade` are smoke size and
  expose the named output at top level.
  - Blade cannot print maskagg's `y`: a runtime-extent array (`Array<Float64 like Idx<?>>`)
    has NO printed form. `--print y` refuses with BL7004 "has no printed form", and a
    `method_for(y) <@> id |> compute` copy has the same type. That is a finding: a compacted
    output cannot be dumped.
  - So the maskagg dump prints an order-sensitive fingerprint instead: m, y(0), y(m-1), two
    Horner left folds (`0.5 a + v`, `0.999 a + v`) and the sum.
- **Validation.** `bench/validation/validate_B.py` builds and runs the dumps (C++ lane,
  serial, `BLADE_EXE_CACHE=0`) and compares each with numpy at rel tol 1e-12. Result,
  2026-10-17: **ALL OK**.
  - stencil lap: 1131 cells, worst 4.9e-15.
  - recur trajectory: 2321 cells, worst 4.8e-15.
  - maskagg: m = 15012 exact; first/last/Horner/sum agree to <= 2.6e-15.
  - checksums: all <= 1.5e-15.

### M10: store audit

FULL-size executables, compiled only and never run, in both lanes, serial, licensed
regime (`BLADE_FP_REASSOC=1`; LLVM with `BLADE_FP_CONTRACT=fast`). `objdump -d` of each,
loops summarized by `<scratchpad>/asmfn.py`; listings in `<scratchpad>/audit/licensed/`.

| kernel | named output | blade-cpp production loop | blade-llvm production loop |
|---|---|---|---|
| saxpy | y2 | `kernel()`: 4-wide `vfmadd213pd` + `vmovupd %ymm,(mem)`; STORES PRESENT | `main`: 16x-unrolled `vfmadd213pd` + 16 `vmovapd %ymm,(mem)` per iteration; STORES PRESENT |
| stencil | lap | `kernel()` inner loop: `vaddpd` x3 + `vfmadd` + 1 `vmovupd` store per 4 cells; STORES PRESENT | `main`: 4 `vfmadd`/12 `vaddpd` + 4 vector stores per iteration; STORES PRESENT |
| recur | trajectory | per step: `step()` stage loops (vector stores into each stage temporary), then a vector copy loop into the trajectory row (`vmovupd` store), after a `memset` zero-fill; STORES PRESENT | `main` (all inlined): 8-store stage loops plus a 4-store row-copy loop; STORES PRESENT |
| maskagg | y | `main`: branchless gather into `y_compact` (one scalar store per input cell, write index advanced by the mask bit: an 11-instruction loop with 1 `vmovsd` store), after the mask and hash-index build; STORES PRESENT | no LLVM build: the lane refuses `compound`, and the fallback exe is byte-identical to blade-cpp (same addresses) |
| fusedchain | (scalar only) | one loop: `vdivpd` + `vfmadd*pd` vectorized; the fold stays a scalar `vaddsd` chain (F11) | one loop, 4x unrolled `vdivpd`/`vfmadd`, the fold in vector lanes (`fadd reassoc`) |
| onepass | (scalar only) | one loop: powers in `vmulpd`, four scalar `vaddsd` chains (16 per iteration) | one loop: `vfmadd`/`vaddpd` lanes (24 packed adds per iteration) |
| dot | (scalar only) | one loop: `vmulpd` + 4 scalar `vaddsd` per 4 cells (serial chain) | one loop: 16 `vfmadd*pd` per iteration (lanes) |

No arm eliminated a named-output store. No transcendental call appears in any production
loop: the spec's fusedchain chain is affine, abs and division only (no `exp`; the `exp` in
my Task-1 probe chain is not the spec's).

**M8, generation loop.** The two Blade lanes generate the SAME bits at different cost:
- C++ lane: the LCG loop is SCALAR in every kernel (`imul` chain, one `vmovsd` store per
  cell);
- LLVM lane: the LCG loop is VECTORIZED (4-wide; six `vpmuludq`-based 64-bit multiplies,
  one `vmovapd` store per 4 cells).
The `inputonly` twins measure each lane's own generation cost (README M8/M9).

### F11 evidence under both FP knobs

`<scratchpad>/foldform.sh`: full-size `blade emit` (.cpp) and `BLADE_LLVM=1 blade emit`
(.ll), serial. This is recorded from the emitted text because `blade plan` does not show
the licensed fold form (F15). "Input" folds are the `input_checksum` sums.

| kernel | REASSOC=0 (repro): cpp / ll | REASSOC=1 (licensed): cpp | REASSOC=1 (licensed): ll |
|---|---|---|---|
| fusedchain | serial / no `reassoc` (0 of 21 `fadd`) | kernel fold SERIAL; only the input fold gets 8 lanes | `fadd reassoc` x2 (kernel fold + input) |
| onepass | serial / 0 of 9 | kernel join SERIAL; input fold 8 lanes | x5 (4 legs + input) |
| dot | serial / 0 of 5 | kernel fold SERIAL; 2 input folds 8 lanes | x3 (kernel + 2 inputs) |
| saxpy | serial / 0 of 6 | checksum fold SERIAL; 2 input folds 8 lanes | x3 (checksum + 2 inputs) |
| stencil | serial / 0 of 8 | per-row prodsum `BLADE_OMP_SIMD_REDUCTION` (3 sites) + input lanes | x4 |
| recur | serial / 0 of 11 | per-row prodsum `BLADE_OMP_SIMD_REDUCTION` (3 sites) | x3 |
| maskagg | serial / (LLVM refused) | `y * y` fold and input fold get 8 lanes | (refused) |

So in the `licensed` headline regime:
- **blade-cpp** reassociates nothing in the kernels of fusedchain, onepass, dot and saxpy
  (F11). Their blade-cpp licensed cells equal repro.
- **blade-llvm** reassociates every fold.
- stencil and recur get simd lanes in both lanes.

### M24: which loop `blade plan` threads (mt8 build, knob unset)

- **fusedchain:** inputs map (`BLADE_OMP_PARALLEL_FOR`, level 0) AND the kernel fold (Path
  B, chunked per thread).
- **onepass:** inputs map ONLY. The `<&!>` join is unthreaded (F4): "parallel preparation,
  serial kernel".
- **stencil:** inputs map; the `lap` halo map (`BLADE_OMP_PARALLEL_FOR`, row loop, level
  0); the checksum fold (Path B).
- **recur:** nothing threaded (no licence; serial-only kernel). Plan shows only fusion
  decisions inside `rhs`/`step`, plus pool-reuse declined in `step`.
- **maskagg:** nothing threaded (serial-only kernel; no plan decisions at all).
- **dot:** inputs map; kernel fold (Path B).
- **saxpy:** inputs map; the `y2` map (`BLADE_OMP_PARALLEL_FOR_SIMD`, flat sweep); the
  checksum fold (Path B).

In every kernel the `input_checksum` folds stay serial, and the blade-llvm arm has no thread
form at all (F2).
