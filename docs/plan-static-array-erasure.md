# Static `Array<T,N>` erasure — investigation and verdict

**Status: REFUTED. Do not build this.** Measured 2026-08-05 at master `8d16eae`
(Ryzen 5800HS, ucrt64 g++, `-O3 -march=native -ffp-contract=fast -fopenmp
-std=c++17`). The premise — that the emitted `Array<T,N>` struct is a residual
cost in hot loops that a static/erased value type would remove — does not
survive measurement. GCC's SROA already erases the wrapper completely: at both
flagship shapes the *emitted* nest and a *hand-written fully-erased twin*
compile to **byte-identical assembly**.

The cost that the "raw pointer" rewrites actually bought back is **the literal
trip count**, not the pointer and not the struct. A separated probe puts a
number on each axis: literal-vs-runtime bound is **1.77x** on a short fiber;
struct-vs-raw-pointer is **1–3%** (noise) in every regime tested, long or
short, simd or K-lane.

So the actionable follow-on is *not* an erasure ABI. It is finishing literal
extent coverage in the ~10 emitters that still hardcode `.extents[d]` in a loop
header without consulting `literalOrRuntimeExtent`, and widening Phase 4's
declines. That is section 6.

---

## 1. Today's ABI

`src/cpp/nested_array_types.hpp:27-42`:

```cpp
template<typename T, size_t N>
struct Array {
    typename promote<T, N>::type data;   // T*, T**, T***, … (N pointer levels)
    const size_t* extents;               // POINTER to a shape table, not the table
    constexpr auto& operator[](size_t i) const { return data[i]; }
    constexpr auto& operator[](size_t i)       { return data[i]; }
    constexpr operator typename promote<T, N>::type() const { return data; }
};
```

Two pointers, 16 bytes, non-owning. Sibling wrappers `Ragged<T>` (`:75`),
`Compound<T,RANK>` (`:105`) and `Sparse<T,RANK>` (`:141`) carry more metadata
but are not on any of the hot paths investigated here.

**Where the extents table lives.** `allocate<TYPE,SYMM,DIAGONALS>(extents)`
(`nested_array_utilities.hpp:235`) and `allocate_strict<>` (`:330`) **read** the
table to build the pointer skeleton over one contiguous pool; they never store
it. The wrapper is brace-initialized by the *emitter*, which bundles the pool
with whatever table it chose to make. Three kinds exist today:

| kind | storage | emitted at |
|---|---|---|
| `static constexpr const size_t X_extents[R] = {…}` | binary (`.rodata`) | array literals `CodeGen.fs:7876`, `:7930`; ragged literal tables `:7767-7769`, `:7806-7808`; symm vectors `:155` |
| `size_t* X_extents = new size_t[R]` | heap, scope-registered teardown | combinator/materialization outputs `CodeGen.fs:8523`, `:8695`, `:8891`, `:9060`, `:9807`, `:10366`, `:10636` |
| `size_t X_extents[R] = {…}` | stack frame | intrinsic paths `CodeGen.fs:12226`, `:12577`, `:3188` — only where the wrapper provably does not escape |

**What touches the struct inside a hot loop.** Exactly three things:

1. the subscript `A[i]` → `data[i]` (`operator[]`, always inlined);
2. the per-level peel
   `Array<T,1> A__i0 = { A.data[i0], A.extents + 1 };`
   (`CodeGen.fs:4850`), one per operand per nest level;
3. the loop bound `A.extents[d]`, when the extent is not a literal in the IR
   (`genLoopBoundExpr`, `CodeGen.fs:5289`).

**Which of those the optimizer already sinks: (1) and (2), entirely.** See
section 3 — the wrapper is scalarized out of existence. (3) it cannot sink:
an opaque bound is a real dependence on memory and, more importantly, a trip
count the vectorizer must handle with a runtime prologue/remainder.

`restrictPeelSites` (`CodeGen.fs:4880-4911`) already drops the wrapper for a
raw `T* BLADE_RESTRICT` row *when the peel's only consumer is a deeper scalar
leaf*. It deliberately declines when the peeled rank-1 local is consumed **as
an array** by the kernel body — which is exactly the comoment3 fiber-kernel
shape. That decline turns out to cost nothing (section 3).

---

## 2. The known blocker, stated precisely

`docs/plan-cpp-perf-exploitation.md:748-755` records why Phase 4's
`static constexpr` extents-table item was skipped, and `tests/corpus/functions/027_loop_array_return.blade`
pins the constraint:

> `Array<T,R>` stores a POINTER to its extents table, so a frame-local
> `size_t[R]` table made the wrapper unreturnable: the heap data pool survived
> the frame but the shape pointer dangled.

**The constraint any design must satisfy:** *every table an `Array<T,R>` value
points at must outlive every wrapper that names it, including across a function
return and including copies of that wrapper made by the caller.* Today's answer
is heap tables plus scope-registered teardown (`TrackedAlloc.PoolAlloc`'s
`OwnedExtentsName`, `CodeGen.fs:7375-7385`).

**A `static constexpr` table satisfies this constraint strictly more safely
than the heap does** — static storage duration outlives everything, and there
is nothing to free. `functions/027` is *not* an argument against constexpr
tables; the rectangular-literal path already returns wrappers pointing at
`static constexpr` tables (`CodeGen.fs:7868-7877` says so explicitly, and cites
the function-return case as the *reason* it is constexpr).

The real reason Phase 4 stopped short is mechanical, not semantic: inside a
spec the return-extents table is *filled by assignment*
(`__ret…_extents[0] = 5;`, `CodeGen.fs:10366-10367` `heapExtents`), and a
`constexpr` table needs a brace initializer at its declaration. Converting is a
shape change to one helper, not a lifetime problem. It is also, per section 3,
worth zero at runtime.

---

## 3. The measured ceiling

Protocol: all variants live in one process, run **round-robin with rotating
order**, 9 timed reps after a warmup, medians reported; 3 independent process
rounds; `OMP_NUM_THREADS=1`; no power-of-two extents. Checksums identical
across variants in every table below. Sources in the session scratchpad
(`erase/drv_dot.cpp`, `drv_com3.cpp`, `drv_short.cpp`, `drv_lane.cpp`).

### 3a. dot, n = 10 000 019, `BLADE_FP_REASSOC=1`

The premise in the brief ("x[i]*y[i] via Array subscripts") is **out of date at
master**. `a3fa63d`/`8d16eae` already emit this, verbatim:

```cpp
// reduce over computation: accumulator loop (omp simd reduction, BLADE_FP_REASSOC, 2 operand streams)
{
    const size_t __rhi = 10000019;
    const double* BLADE_RESTRICT __rsrc0 = x.data;
    const double* BLADE_RESTRICT __rsrc1 = y.data;
    BLADE_OMP_SIMD_REDUCTION(+:s)
    for (size_t __i0 = 0; __i0 < __rhi; __i0++) { … }
}
```

Raw restrict pointers, literal bound, no struct in the loop. It is *already*
the erased form.

| variant | median ms (3 rounds) |
|---|---|
| emitted (verbatim) | 5.30 / 4.70 / 5.78 |
| erased twin — independent `malloc` buffers, no `Array` on the path | 5.25 / 5.35 / 5.26 |
| struct-subscript + runtime `x.extents[0]` bound (pre-`a3fa63d` shape) | 5.46 / 4.72 / 5.60 |

**Delta: zero.** Round-to-round drift (~15%) dwarfs every variant difference.

*Assembly*: `v0_emitted` and `v1_raw` are **identical instruction for
instruction** (25 insns, 3 `vfmadd`, 1 `vmulpd`, 7 `ymm`); the only textual
difference is the function label. The struct-subscript form is 55 insns, but
the extra 30 are the cold runtime-remainder scalar tail forced by the non-literal
trip count — the vector core is the same, which is why it also times the same at
n = 10⁷.

Knob-*off* (shipping default) emits a serial chain with struct subscripts and a
literal bound; its cost is the serial dependence, not the struct.

### 3b. comoment3, 61 × 2003 triangular 3-operand prodsum nest

Emitted at master (`prodsum(a,b,c)/2003` under `where comm(a,b,c), omp(a:1)`):

```cpp
#pragma omp parallel for schedule(dynamic)
for (size_t __i0 = 0; __i0 < 61; __i0++) {
    Array<double, 1> Rc____i0 = { Rc.data[__i0], Rc.extents + 1 };
    for (size_t __i1 = 0; __i1 < 61 - __i0; __i1++) {
        Array<double, 1> Rc____i1 = { Rc.data[__i1 + __i0], Rc.extents + 1 };
        double* BLADE_RESTRICT __orow_C3 = C3[__i0][__i1];
        for (size_t __i2 = 0; __i2 < 61 - __i1 - __i0; __i2++) {
            Array<double, 1> Rc____i2 = { Rc.data[__i2 + __i1 + __i0], Rc.extents + 1 };
            __orow_C3[__i2] = ([&]{ const size_t __pn = 2003; double __ps = 0;
                BLADE_OMP_SIMD_REDUCTION(+:__ps)
                for (size_t __pt = 0; __pt < __pn; __pt++)
                    __ps = __ps + Rc____i0[__pt] * Rc____i1[__pt] * Rc____i2[__pt];
                return __ps; }() / 2003.0);
        }
    }
}
```

Three `Array<double,1>` operand wrappers, none restrict-qualified; only the
output row is.

| variant | median ms (3 rounds) |
|---|---|
| emitted (verbatim) | 17.52 / 15.66 / 16.29 |
| erased — `const double* BLADE_RESTRICT` rows from `Rc.data[i]`, literal bound | 17.35 / 15.64 / 16.31 |
| erased flat — pool base + literal stride, no row table, no struct | 17.34 / 15.57 / 16.31 |

**Delta: ≤ 1%, i.e. zero.**

*Assembly*: the emitted OMP outlined body and the erased-row-pointer body are
**byte-identical apart from the function label** (116 insns each, same
`vfmadd`/`vmulpd`/`ymm` counts). The fully-flat variant differs by 3
instructions — the address arithmetic that replaced the row-table loads — with
an identical vector mix.

### 3c. What the struct rewrites were actually buying — the separated probe

Two axes crossed independently, on a *short* fiber where the trip count is the
binding constraint (p = 1009 rows × N = 13 samples, dense pair prodsum):

| operand form | bound | median ms (3 rounds) |
|---|---|---|
| `Array<double,1>` wrapper | `a.extents[0]` (runtime) | 3.47 / 3.44 / 3.63 |
| `Array<double,1>` wrapper | `13` (literal) | **1.98 / 1.95 / 2.09** |
| `const double* BLADE_RESTRICT` | runtime | 3.43 / 3.46 / 3.65 |
| `const double* BLADE_RESTRICT` | `13` (literal) | **1.92 / 1.90 / 2.04** |

- **literal bound: 1.77x**, in both operand forms;
- **struct → raw restrict pointer: 1–3%**, in both bound forms.

The two axes are independent and only one of them is worth anything. This is
the central result of the investigation.

### 3d. The K-lane arm (where restrict/SLP was thought to matter)

`fpReassocSimdOp` declines for a `comm`-declared kernel whose combine is a
call, falling back to K named scalar lanes (`CodeGen.fs:13761-13779`) — the arm
the original "K=8 was 2.63x slower through Array peels" observation came from.
Same comoment3 shape, struct operands vs raw restrict operands, everything else
identical:

| lanes | struct operands | raw restrict operands |
|---|---|---|
| K = 5 | 35.7 / 34.9 / 34.1 ms | 35.3 / 33.6 / 34.0 ms |
| K = 8 | 15.7 / 17.2 / 14.9 ms | 15.8 / 17.3 / 15.1 ms |

Parity at both lane counts. (This probe inlines the combine rather than routing
it through the emitted `__wrap_` lambda, so it is **not** evidence about the
`laneCountForStreams` policy and should not be read as re-litigating it. It is
evidence only about struct-vs-pointer, which is what it varies.)

### 3e. Ceiling summary

| benchmark op | current | fully-erased twin | prize |
|---|---|---|---|
| dot, n = 10 000 019, reassoc | 5.3 ms | 5.3 ms | **0** |
| comoment3, 61 × 2003, ST | 16.3 ms | 16.3 ms | **0** |
| short fiber (N = 13), literal bound already | 1.95 ms | 1.92 ms | **~1.5%** |
| short fiber (N = 13), runtime bound | 3.44 ms | — (bake the bound) | **1.77x, from the bound alone** |

---

## 4. Why the struct is free (mechanism)

GCC's SROA scalarizes `Array<T,N>` because every property it needs holds by
construction:

- the type is a two-pointer aggregate with no user constructor, destructor or
  virtual anything, so it is trivially copyable and never address-taken;
- `operator[]` is `constexpr` and one line, always inlined;
- every construction site is a brace initializer over expressions already in
  registers (`{ A.data[i], A.extents + 1 }`), so after inlining the "struct" is
  two SSA names;
- the wrapper never escapes the loop nest — it is consumed by subscripts and,
  where a kernel is a real function, that function is inlined at the site.

The aliasing worry is also already handled, and not by erasure: the **output**
row is restrict-qualified (`__orow_C3`, `CodeGen.fs:5802`), which is sufficient
to disambiguate the store from the reads. Restrict on the *read* operands adds
nothing — read/read aliasing was never a barrier.

What SROA cannot do is invent a trip count. A `for (t = 0; t < n; t++)` with
opaque `n` forces GCC to emit an alignment/remainder scaffold and to keep the
loop counted rather than fully unrolling it; on a 13-iteration fiber the
scaffold is most of the work. That is the entire 1.77x.

---

## 5. Design options, costed

### (i) Status quo plus — extend the existing pattern *selectively*

Split the pattern into its two halves, because they have different value:

- **restrict aliases on read operands / raw-pointer peels: STOP.** Measured
  worth 1–3% (noise) in every regime; byte-identical assembly at the two
  flagship sites. The ~8 sites already carrying it do no harm and should stay
  (they cost nothing and document intent), but extending the *analysis*
  (`restrictPeelSites`) to cover fiber-kernel peels would be pure complexity
  for zero measurable gain. **Effort to extend: M. Payoff: 0.**

- **literal bounds: CONTINUE, and finish it.** Worth up to 1.77x wherever the
  fiber is short. `genLoopBoundExpr` (`CodeGen.fs:5251-5289`) already bakes
  `IRLit`, and the intrinsic/IIFE emitters go through `literalOrRuntimeExtent`
  (`CodeGen.fs:1676`, 16 call sites). The residue is the *specialised*
  emitters, which hardcode `sprintf "%s.extents[%d]"`:

  | emitter | site |
  |---|---|
  | transpose — extents table + loop headers | `CodeGen.fs:3365`, `:3373` |
  | stack / expand | `CodeGen.fs:3413`, `:3425` |
  | join | `CodeGen.fs:3454`, `:3459`, `:3469`, `:3479` |
  | decompact (`nExpr`) | `CodeGen.fs:3580`, and the loops at `:3618`, `:3820` |
  | output-extents copies | `CodeGen.fs:3882`, `:3951` |
  | gram / matmul `n` | `CodeGen.fs:4379`, `:4516` |
  | `VirtualReverse` extent | `CodeGen.fs:4716` |
  | fused-joint `extAt` | `CodeGen.fs:4734` |
  | gemv dispatch trailing extent | `CodeGen.fs:6504`, `:6585` |
  | `genApplyCombinator` outer-product loops | `CodeGen.fs:10375-10376` |

  **Corpus survey** (161 programs across `loops`, `math`, `symmetry`,
  `index-types`, `func-arrays`, emitted at master with `BLADE_FP_REASSOC=1`;
  counting `for (size_t __… ; … < …)` headers): **83 of 472 (17.6%) still carry
  a runtime `.extents[]` bound.** By slice: `symmetry` 0/84, `func-arrays`
  0/18, `loops` 8/64, `math` 24/198, `index-types` 51/108. They concentrate in
  exactly the emitters above — `hosvd` (8 in one program, all transpose),
  `decompact_*` (3 each), `transpose_rank3` (3), `ragged_*` (3–4, genuinely
  runtime).

  Note this measures *sites*, not *time*: many are long loops where the bound
  is worth nothing, and some (ragged, compound cardinality, provider reads) are
  genuinely runtime and can never bake. The exploitable subset is "short inner
  extent, statically known". **Effort: S** — each site is a one-line swap to
  `literalOrRuntimeExtentOfArray`, and the shared helper already enforces the
  packed-record `Rank <> 1` decline that a hand-rolled version would get wrong.

- **The bigger literal-bound lever is Phase 4 coverage, not new call sites.**
  `genLoopBoundExpr` bakes whenever the IR extent *is* `IRLit`; where it is not,
  Phase 4 declined. The declines are enumerated in
  `plan-cpp-perf-exploitation.md:737-747`: cap of 4 per function, recursive and
  mutually recursive callees, cross-module call sites, partially-pinned
  signatures, and lifted lambdas inside a spec (whose own index records keep
  symbolic extents). **Effort: M–L**, and it is the only path with real upside
  left.

### (ii) `StaticArray<double, 10007>` — a static-shape value type

Shape would be: `template<typename T, size_t... Ns> struct StaticArray { typename promote<T,sizeof...(Ns)>::type data; static constexpr size_t extents[] = {Ns...}; };`
plus an implicit `operator Array<T,N>()` producing `{ data, extents }` — which
is safe precisely because the constexpr table has static storage duration, so
**`functions/027`'s constraint becomes trivially satisfied** (section 2). No
two-overload split is needed; every existing `Array<T,N>` consumer keeps
working through the conversion, and the monomorphized-per-shape copies Phase 4
already makes give it a natural home.

So it is buildable and the lifetime objection genuinely evaporates. **It is
still the wrong thing to build**, for one reason: the win it would deliver is
*the constexpr extents*, and Phase 4 already delivers exactly that information
**at the F# level, as a literal in the emitted text**, which is where GCC
consumes it. A C++-level constexpr table is a strictly weaker way to say the
same thing (GCC must fold `extents[0]` through the class, versus reading `2003`
in the loop header), and it buys nothing over the erasure already measured at
0%. Costs: a second array type through every emitter, a conversion at every
mixed call, an extra monomorphization axis, and a new `-fpermissive`-class
portability surface for MSVC/nvcc (cf. `blade_portability.hpp`). **Effort: L.
Payoff: 0 beyond what Phase 4 already gives.** Reject.

### (iii) Full constexpr erasure (C++20 constexpr containers)

Reject in one line: `constexpr` allocation affects *constant evaluation*, not
generated code, so it cannot speed up a loop at all; it would require moving
the whole toolchain off `-std=c++17` (currently pinned in `Benchmarks.fs:96`
and every driver); and it is structurally impossible for the case that matters
— arrays whose data arrives from a provider/SQL/NetCDF read at runtime.

---

## 6. Recommendation

**Take (i)-narrow: literal bounds, not struct erasure. Drop the erasure track
entirely.**

Implementation sketch, in priority order:

1. **Route the residual emitters through `literalOrRuntimeExtentOfArray`**
   (`CodeGen.fs:1565`). Highest-value first, since these govern *inner* loop
   trip counts:
   - decompact `nExpr` — `CodeGen.fs:3580` (and the derived bounds at `:3618`,
     `:3820`); 51 of the 83 residual headers are in the `index-types` slice and
     most are decompact/transpose;
   - transpose — `CodeGen.fs:3359-3373` (`srcVar`/loop) and the extents-table
     copy at `:3365`;
   - `VirtualReverse` — `CodeGen.fs:4716`, which already has the literal in
     `level.Extent` and pattern-matches `IRLit` itself but falls through to
     `.extents[]` for the non-literal case; it is one arm short of the shared
     helper;
   - fused-joint `extAt` — `CodeGen.fs:4734`;
   - `genApplyCombinator` outer-product headers — `CodeGen.fs:10375-10376`.
   Each is a one-line change; the helper's `Rank <> 1` and short-record
   declines already cover the packed-record hazard. **Effort: S.**
   Verification: a corpus re-emit and a re-count of the 83 residual headers,
   plus `blade test loops indextypes math symmetry`.

2. **Cheap, unrelated-but-adjacent cleanup**: in a shape spec, emit the
   return-extents table as `static constexpr const size_t X_extents[R] = {…}`
   instead of `new size_t[R]` + assignments (`heapExtents`,
   `CodeGen.fs:10365-10367`), and skip the registered free. This is what
   `plan-cpp-perf-exploitation.md:748-755` left open. It is **safe** — static
   storage duration strictly dominates the heap for the `functions/027`
   constraint — and it removes an allocation + free per materialization, but it
   is **not** a performance item at the shapes measured here. **Effort: S.**
   Do it for the allocation-count and teardown-simplicity reason, not for speed.

3. **Widen Phase 4** (partial/cross-module specialization, raise or drop the
   cap of 4, revisit the recursive decline). This is where the remaining 1.77x
   actually lives, for programs whose fibers are short and whose extents are
   symbolic. **Effort: M–L.** Gate it on finding a real program in that regime
   first — none of the current benchmark ops is one.

**Payoff per benchmark op, honestly:**

| op | expected gain from any of this |
|---|---|
| dot (n = 10 000 019) | **0** — already literal-bounded and already erased by SROA |
| comoment3 (61 × 2003) | **0** — same |
| gemv / gemm / gram native | **0** from this work — their gaps are blocking/threading, not shape |
| decompact / transpose / hosvd programs | small, and only where an inner extent is both short and statically known |
| a hypothetical short-fiber kernel over `Idx<n>` with n literal at the call site | up to **1.77x** — and Phase 4 already covers the common case of it |

**Effort grade for the recommended path: S** (item 1, plus item 2 as a
freebie). **The erasure design itself: not graded — do not build it.**

---

## 7. What would change this verdict

Three things, none currently observed:

- A compiler that does **not** do SROA on the wrapper (MSVC, nvcc device code).
  All measurements here are ucrt64 g++. If `blade test cuda` or an MSVC build
  ever shows a struct-shaped regression, re-run `drv_com3.cpp` under that
  compiler before concluding anything.
- A wrapper that stops being trivially copyable — adding an owner flag, a
  destructor, or a virtual anything to `Array<T,N>` would forfeit the free
  erasure this whole verdict rests on. Treat `nested_array_types.hpp:27-42` as
  performance-load-bearing in that specific sense.
- An `Array<T,N>` that genuinely **escapes** a hot loop (stored into a
  container, passed to a non-inlinable function, address-taken). None of the
  emitted shapes surveyed does this; a future first-class-array feature might.
