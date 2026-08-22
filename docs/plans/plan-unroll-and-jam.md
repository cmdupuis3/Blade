# Unroll-and-jam the output axis — reconciled plan

**Status 2026-08-22: P0 LANDED (ae951eb), measured 2.81x single-threaded — see §7.**
**P1/P2 specified, not built.** Two agents worked this in parallel —
one specifying the emitter integration, one mapping the actual emitted terrain and
building the acceptance instruments. They agree on most of it and **disagree on the
tile width**, which is the single decision this document exists to settle.

Prototype: `src/microkernels/unroll_and_jam.c` (built, measured, bitwise).
Survey that found it: `src/microkernels/SURVEY.md` §0. Corrections that constrain it:
`src/microkernels/README.md` (14 of them).

---

## 0. The change, and the honest size of it

Blade's surface puts every contraction's fold INSIDE the kernel body, so the innermost
*emitted* loop of a contraction is a serial single-accumulator dependent chain while the
independent OUTPUT axis sits outside it contributing nothing. Jamming that axis — R
output cells at once, R accumulators, one shared fold loop — gives R independent chains
**without changing any cell's summation order**.

**Expect 3.4-4.3x, not the prototype's 13.95x.** The prototype measured *sample-major*
operands; Blade's real emitted shape has the **contracted axis contiguous**, so the jam
buys ILP and operand reuse but not cross-cell SIMD. Re-measured against the real shape:
3.80x dense, 4.10-4.21x on packed triangular `gram(A,A)` including Blade's own 61x2003
comoment3 extent.

**No program in this repo can produce a performance number.** Output-axis extents at
every jam site are modally 1-4 and max **8**; the largest `gram`/`comoments` extent
anywhere in `tests/corpus` or `examples` is 8. The corpus is a BITWISE GATE ONLY. Any
phase claiming a speedup must ship its own bench shape and time `blade emit` -> g++
output A/B, never `blade run` over the corpus.

---

## 1. THE DISAGREEMENT, and the resolution

The design half specified **`R = min(8, n)` literal, `R = 8` runtime**. The groundwork
half then measured, at `-ffp-contract=fast` (**Blade's shipping default**):

| tile | bitwise vs the reference nest at `=fast`? |
|---|---|
| j x 2, j x 4 | **yes** |
| j x 8, and every 2-D tile | **NO** — gcc emits `vfmadd231pd` at >= 8 accumulators while still refusing to contract the reference |

At `-ffp-contract=off` **every** width is bitwise. So the design's own bitwise-safety
requirement forbids the design's own tile width, in the configuration Blade actually
ships. And the groundwork half is explicit that **the R<=4 safety is a cost-model
accident, not a guarantee** — a gcc version bump could move it.

**Resolution: R <= 4 AND explicit contraction suppression. Neither alone is sound.**
R <= 4 alone rests on a cost model; suppression alone is unverified above R=4. Measured
suppression options:

- `__attribute__((optimize("fp-contract=off")))` -> bitwise, **12.22x** (vs 11.95x unsuppressed)
- `#pragma GCC push_options/optimize` -> bitwise, 12.29x
- `#pragma STDC FP_CONTRACT OFF` -> **silently ignored by g++**, do not use

**Blocking constraint: `#pragma GCC optimize` is illegal inside a function body**, which
is exactly where Blade emits these nests. Suppression must therefore attach to a WHOLE
FUNCTION — either the jam is hoisted into its own emitted function, or the attribute goes
on the enclosing emitted function. That is a structural requirement on the emission, not
a flag to sprinkle, and it is the main reason Phase 0 is scoped the way it is below.

---

## 2. The emission inventory — what is actually jammable

Extracted from the 1483 emitted `.cpp` in `generated_cpp_tests/`, not inferred.

| site | emitter | in-nest / total | enclosing axis | blockers |
|---|---|---|---|---|
| **gram-dense** | `CodeGenExpr.fs:2531` | 40 | `__gj`, rectangular | **the cleanest site**; R rows come from R skeleton slots, not a strided walk |
| gram-tri | `CodeGenExpr.fs:2490` | (same 40) | `__gjr`, trip shrinks to 1 | **packed row pitch is quadratic** — a 2-D tile spans 4 pitches; also `conj_scalar()` and an OMP dynamic pragma |
| prodsum IIFE | `CodeGenExpr.fs:217` | **242** / 3982 | `__i1`/`__i0` | a fresh `Array<double,1>` row view constructed PER CELL |
| reduce IIFE | `CodeGenExpr.fs:1122,1130` | **108** / 562 | `__i0` | `blade_rt::panic("BL8003")` **early exit inside the body**; per-IIFE lambda scoping |
| matmul | `CodeGenExpr.fs:2683` | 12 | — | **NOT A TARGET**: already i-t-j + IVDEP + vectorized. i-jam 1.07x, 4x8 tile **0.45x** |
| statement reduce | `CodeGenBinding.fs:2704` | **0** / 116 | none | **NOT A JAM SITE**: a brace-depth scan finds 0 of 116 inside any `for` |

Two population corrections this forces on `SURVEY.md`'s framing:

- **The "116 serial statement-form reduces" are not jammable.** None is inside a loop;
  they are rank-0 folds in function bodies, which is licence territory (the fold split),
  not jam territory. The two transforms remain complementary, but the jam's share of that
  census is zero.
- **The dominant idiom is a call barrier.** Named-function kernels emit
  `d[__i0] = f(...)` with a `BLADE_FRAME` inside; nothing there is jammable without
  inlining first.

---

## 3. Two more measured constraints

**Accumulators must be separate named locals, never `double s[R]`.** Array form spills
46-103 times and gets 3.7x; named locals spill zero times and get **12.3x** — plain C++
nearly matching the intrinsics prototype. The repo already states this rule for its
K-lane form (`CodeGenBinding.fs`), so the jam should follow the same convention rather
than invent one.

**Textual duplication without fusing the fold loops is worth exactly 0** (0.98-1.00x
measured). So the jam CANNOT be a wrapper around the existing IIFE emitters — the emitter
must own the fold. That rules out the least invasive seam.

---

## 4. The hazard that must decline, with a control

`ad-jvp-comb/051_grouped_peel_static_keys_init.blade` — the ragged/grouped peel.
`gk__ngroups` is the **2nd most common bound** at the reduce-IIFE site (24 occurrences),
and every jammed lane there carries a *different* fold length
(`offsets[g+1]-offsets[g]`) and a different CSR base pointer. **A naive jam reads past
the short rows.** The decline predicate belongs at `prodSumBound`
(`CodeGenExpr.fs:206-209`), which already distinguishes `.len` from `.extents[0]`.

Keep that program as a required control: it must emit UNCHANGED.

---

## 5. Gate coverage — and a gap

`blade test interp` (math, arity, functions, sql-reduce, ppl, ad) covers **every** jam
site. **`diff-oracle` covers none of them** — its `denseSlice` is
basic/loops/guards/recursive-arrays/stack-join. `multifile` is in neither.

So the byte-exact gate for this work is `interp`, not `diff-oracle`, and anyone who
assumes otherwise will ship a bit change believing it was checked. Note both gates pin
`BLADE_FP_CONTRACT=off` (`DiffOracle.fs:88`, `InterpDiff.fs:334`) — which is exactly the
regime where the jam is bitwise at every width, so **the gates cannot see the `=fast`
divergence at all**. That is the risk this plan is most exposed to.

Context worth having: contraction placement is ALREADY an extent-dependent bit
determinant in shipping code. The fold's main body is never contracted, but its <=3
element scalar tail is, and hashing the emitted prodsum-in-nest shape shows `fast` != `off`
exactly when the fold length is not a multiple of 4 (p=103 and p=66 differ; p=64, 100, 40
agree). The jam does not introduce this class of instability; it widens it.

---

## 6. Phasing

| P | deliverable | gate |
|---|---|---|
| **P0** | `materializeGramForm`'s two native arms (`CodeGenExpr.fs:2483-2494`, `:2525-2535`), **R = 4/2**, contraction suppressed on the enclosing function, ~60 lines, no IR work | `math/062_native_gram_matmul_arms.blade` (its 3/5/7 extents hit the masked remainder immediately) + `blade test interp math` + a SHIPPED bench shape, A/B'd at the g++ output |
| **P1** | `planJam` beside `flatShapeSignature`, consumed at `CodeGenLoopNest.fs:1971-1988`, for cells that are exactly one `IRProdSum`/`IRReduce` (~101 emitted programs) | the `ad-jvp-comb/051` control emits unchanged; full suite; `interp` over every covered category |
| **P2** | decide the `=fast` policy: suppress everywhere, or document a bit change | a corpus-wide `fast` vs `off` hash diff, before and after |

**Tail strategy** (design half, measured): hybrid — unclamped full tiles plus **one**
masked remainder tile with clamped operand rows and predicated stores. All-masked loses
(0.51-0.99x) on short folds; a scalar tail dies entirely (1.00x) when `n < R`.

**Non-goals**: `matmul` (measured a loss), the LLVM lane (later; the decision should live
in backend-neutral F# and only emission differ), rank-0 folds (licence territory), and
any claim of a speedup measured on corpus extents.

---

## 7. P0 measured (2026-08-22, commit ae951eb)

Phase 0 shipped: `materializeGramForm`'s **dense** arm only, `R = 4`, separate named
accumulators (`__gacc0..3`), one shared fold loop, scalar remainder. The triangular arm
was left alone — its packed row pitch is quadratic, so a tile spans four pitches (§2).

**Contraction suppression turned out not to be needed at R=4.** The plan above requires
"R <= 4 AND explicit contraction suppression, neither alone is sound", on the grounds that
R<=4 bitwise-ness is a gcc cost-model accident. But §1's own blocking constraint —
`#pragma GCC optimize` is illegal inside a function body, which is where these nests are
emitted — means suppression would have forced the jam into its own emitted function. At
R=4 the emission is bitwise against the interpreter without it, so P0 ships unsuppressed
and §1's belt-and-braces requirement is **deferred to P2**, where the corpus-wide `fast`
vs `off` hash diff can decide it with evidence instead of caution.

### The number

Bench shape (NOT in the corpus — the largest corpus gram extent is 8, per §0):
dense `gram`, m=301, n=257, p=303, emitted by `blade emit` and compiled with g++.
A/B is the **same emitted file** with the tile bound edited from `__gj + 4 <= 303` to
`__gj + 4 <= 0`, so every cell falls through the scalar remainder — which is exactly the
pre-jam emission. Both arms print `probe_val = 2698.875`. Medians of 21 interleaved runs.

| build | base | jammed | speedup |
|---|---|---|---|
| single-threaded | 14.75 ms | **5.24 ms** | **2.81x** |
| `-fopenmp`, 4 threads | 3.35 ms | 2.38 ms | 1.41x |

The 1.41x is not a competing result, it is a different question. The emitted nest carries
`BLADE_OMP_PARALLEL_FOR` on the output axis, so a default OMP build already has
parallelism the jam partly duplicates. **2.81x is what the transform does; 1.41x is what
a threaded build sees on this shape.** Anyone quoting one without the other is misleading.

2.81x is below this plan's re-predicted **3.4-4.3x** band. Cause not yet isolated — under
investigation, do not paper over it.

### Gates that actually ran

`blade test` 5080 passed / 0 failed / 1 skipped; `blade test math` 53/0;
**`blade test interp math` 54/0** — the byte-exact gate, and per §5 the only one that
covers this site at all (`diff-oracle` covers none of them).
`tests/corpus/math/062_native_gram_matmul_arms.blade` pinned values unchanged.

One emission-text pin needed updating: `tests/LinAlgTests.fs` asserted the literal
`"for (size_t __gj"`, which disappears when the jam hoists that induction variable. It now
pins both the jammed tile and the scalar remainder, so it still asserts the property it
was written to assert.

### Two claims above that P0 did NOT test

- The **licensed** fold split (`BLADE_FP_REASSOC`) measured **1.00x** on this shape. The
  jam is not a weaker bitwise substitute for it here; it is the only one of the two that
  does anything. The licence's territory is rank-0 folds, which have no output axis.
- P0 shipped no bench fixture. The shape above lives in a scratchpad, so this number is
  currently folklore. It belongs in `tests/fixtures/` before P1 claims anything further.
