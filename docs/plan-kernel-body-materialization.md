# Kernel-body materialization — design pass for issue #3

`docs/plan-array-expression-fixes.md` row #3 / Theme B: *"kernel bodies are not
full expression contexts."* It is the last wall between the repo's two
motivating programs (`examples/lsdft.blade`, `examples/lswosa.blade`) and an
end-to-end run, and the one item that plan explicitly deferred for a design
pass rather than a patch.

Written 2026-08-09 against `master` @ `3500258`. Method as in
`docs/plan-tuples-vs-arg-packs.md`: every behavioural claim below was compiled
and/or run against a private build
(`dotnet build -c Release -o C:\bt_p3\bin …`), with the emitted `.cpp` read as
ground truth. Repros are inlined so this stays useful after the scratch files
are gone.

---

## 0. Recommendation in one page

**The headline measurement is that the hard part already works.** A kernel
body that materializes an array compiles to *correct* C++ today — whenever the
body is emitted through the **lifted-function statement form**
(`genFuncBodyScoped`, `CodeGen.fs:15083`). Captures, nesting, per-row
allocation, `prodsum` over the local, and the outer function's own params all
come out right. Verified end-to-end, values and all:

```blade
let i = complex(0.0, 1.0)
let ts: Array<Float like Idx<4>> = [0.0, 0.5, 1.0, 1.5]
let ws: Array<Float like Idx<3>> = [1.0, 2.0, 3.0]
function inner(w: Float64, tt: Array<Float like Idx<4>>, ii: Complex128) = {
    let e = (exp <@> (ii * w * tt)) |> compute   // array-valued local
    prodsum(e, e)
}
let out = ws <@> lambda(w) -> inner(w, ts, i) |> compute
// out = [(0.134162972720552,1.89188841969345),
//        (0.890379829239612,-0.126920566681172),
//        (0.0590475281652436,0.273822995102698)]      RUNS TODAY
```

Move the same two lines *inside* the lambda and it dies. Nothing about the
computation changed — only which emitter rendered the body.

So issue #3 is not "invent a representation for kernel-local arrays". It is
three much smaller things:

| | Recommendation |
|---|---|
| **Representation** | Keep exactly what the lifted form emits today: a heap `Array<T,R>` allocated on entry to the kernel body and freed on exit, owned by the body's alloc scope. Add **one** optimization — hoist the allocation to the enclosing loop's preamble when the extent expression is outer-iteration-invariant. Measured cost of *not* doing it: **8.1×** (below, §4). |
| **Layer** | Split by manifestation. TypeCheck owns the *shape* gap (M-B): an array-valued intermediate must be array-*typed*. Lowering owns the *forcing* gap (M-D, and the bare-`IRApplyCombinator` half of M-A). CodeGen owns exactly one new decision: **when a kernel body is not expression-shaped, call the lifted callable instead of inlining its text.** No new statement-emission machinery is needed inside kernel lambdas; `genFuncBodyScoped` already is it. |
| **Staging** | S0 module-capture forwarding (prerequisite, already filed) → S1 typecheck shape (M-B) → S2 call-don't-inline + forced lets (M-A, `__v27`) → S3 output-rank grids (M-C) → S4 return forcing (M-D). S4 is *"the callee forces"*, not *"the caller's compute reaches inside"* — laziness provably never crosses a named-function boundary today (§8). |

And one thing this pass found that is **not** in the plan doc: an
array-returning kernel over `method_for` **compiles, runs, and prints garbage**
today — no diagnostic anywhere (§3, M-C1). That is a silent-wrong-answer of the
same class as issue #1 and should be guarded in S0 even if S3 lands later.

---

## 1. The four manifestations, measured

All four reproduced from scratch this session. The *stage* each dies at is the
load-bearing detail, because it tells you which layer owns it.

### M-A — a nested loop-object map bound in a kernel body

```blade
let w: Array<Float like Idx<3>> = [1.0, 2.0, 3.0]
let t: Array<Float like Idx<4>> = [0.0, 0.5, 1.0, 1.5]
let out = w <@> lambda(f) -> {
    let wt = f * t
    let c = sin <@> wt
    reduce(c, (+))
} |> compute
```

`blade check`: **OK**. `blade run`: codegen sentinels. The emitted `.cpp`
contains *two* renderings of the same body:

```cpp
// (a) the LIFTED form -- __lambda_19 -- CORRECT up to the return
double __lambda_19(double f, Array<double, 1>& t) {
    Array<double,1> __v1073741824 = { allocate<…>(__v1073741824_extents), … };
    auto __wrap_13___v1073741824 = [&](double x) { return __lambda_13(x, __v11); };
    for (size_t __i0 = 0; __i0 < 4; __i0++)
        __v1073741824[__i0] = __wrap_13___v1073741824(t[__i0]);
    auto __v5 = __v1073741824;                       // wt: materialized, correct
    auto __retv… = BLADE_CODEGEN_ERROR_REDUCE_OVER_A_DEFERRED_COMPUTATION_…;
    …
}
// (b) the INLINE form, which is what main() actually uses -- three sentinels
__fp_out[__fk] = ([&]() { auto __v5 = ([&]() { auto __v11 = __fp_w[__fk];
    return BLADE_CODEGEN_ERROR_LOOP_OBJECT_USED_AS_VALUE(t); }());
    return ([&]() { auto __v8 = BLADE_CODEGEN_ERROR_UNEVALUATED_COMPUTATION_…;
    return BLADE_CODEGEN_ERROR_REDUCE_OVER_A_DEFERRED_COMPUTATION_…; }()); }());
```

`__lambda_19` is emitted and **never called**. The whole failure is that the
enclosing loop chose (b).

Sentinel sites: `CodeGen.fs:2178` (`IRMethodFor`/`IRObjectFor`),
`CodeGen.fs:2181` (`IRApplyCombinator`/`IRComposeApply`), `CodeGen.fs:2186`
(`IRReduceCompute`) — all in `exprToCppCore`, i.e. all reached *because* the
body is being rendered as an expression.

### M-B — an array-valued intermediate, dead at typecheck

```blade
let m: Array<Float like Idx<2>, Idx<3>> = [[1.0,2.0,3.0],[4.0,5.0,6.0]]
let out = method_for(m) <@> lambda(r: T^1) -> {
    let w = (r * 2.0) |> compute
    prodsum(w, w)
} |> compute
// error[BL3999]: prodsum() requires array arguments
```

**Why a different stage from M-A.** M-A's operands are *concrete* module-level
arrays, so every type resolves and only emission fails. M-B's row param is a
caret-shorthand `T^1` — an arity-constrained **inference var**, not an
`IRTArray` (see `memory/caret-shorthand-arity-vars.md`, and `inferProdSum`'s
own comment at `TypeCheck.fs:5674-5691`). The array-producing arms of
`inferBinOp` / the apply-elem adoption are gated on the operand **resolving**
to an array; against an unmaterialized var they fall to the scalar arm, so the
intermediate is scalar-typed and every array consumer downstream honestly
refuses. Discriminating matrix, all measured:

| kernel body | result |
|---|---|
| `lambda(r: T^1) -> prodsum(r, r)` | **runs**, `out = [14, 77]` |
| `let w = (r * 2.0) \|> compute; prodsum(w, w)` | BL3999 `prodsum() requires array arguments` |
| `let w = r * 2.0; prodsum(w, w)` | BL3999, *identical* — the `\|> compute` is irrelevant |
| `let w = (r * 2.0) \|> compute; reduce(w, (+))` | BL3999 `reduce() requires an array as first argument` |
| `let w = (r * 2.0) \|> compute; extents(w)` | BL3007 `extents() requires an array` |
| `let w = (r * 2.0) \|> compute; w + 1.0` | BL3999 *"Type variable with arity 1 requires a rank-1 array, got IRTScalar ETFloat64"* |
| same body, `r: Array<Float like Idx<3>>` (concrete) | **typechecks**, dies at codegen (M-A's guard) |

The last two rows are the diagnosis: the intermediate really is
`IRTScalar ETFloat64`, and a concrete row annotation makes the whole class
typecheck. `requireArrayArgMinRank` (`TypeCheck.fs:2113`) is the existing
*demand* that materializes such a var; `inferProdSum` already calls it
(`TypeCheck.fs:5692-5697`), which is exactly why bare `prodsum(r, r)` works.
Nothing calls it at the binop / nested-apply seam.

The same root, one level up, with a **concrete** operand:

```blade
let out = ws <@> lambda(w) -> { let e = exp <@> (w * ts); prodsum(e, e) }  // BL3999
let out = ws <@> lambda(w) -> { let e = w * ts;           prodsum(e, e) }  // OK
let out = ws <@> lambda(w) -> { let e = exp <@> (i * w * ts); prodsum(e, e) }  // OK (i complex)
```

`w * ts` leaves an *unconstrained* infer var that `inferProdSum`'s own demand
then materializes. `exp <@> (w * ts)` **resolves** it to a scalar first
(nothing re-materializes it afterwards), so prodsum refuses. `i * w * ts`
escapes because the leading `i` is a resolved complex scalar, so the broadcast
arm fires and a genuine array type exists. This is the array twin of the
Theme C round-2 unit bug — *same* mechanism (an unresolved kernel param
suppresses the array-shaped arm), *different* victim (shape, not units).

`examples/lswosa.blade`'s form of M-B, reproduced standalone:

```blade
let sw = hanning((trow, srow), lo, hi)   // named fn, array return
reduce(sw, (+))                          // BL3999: reduce() requires an array
```

which in the full file surfaces one call later as
`BL3999: argument 1, component 1: the parameter component is declared
Array<Float64 like Idx<__inferred_n>> (rank 1) but the argument component is
Float64 (rank 0)` at `lswosa.blade:114` — `sw` handed to `wosa_lsdft`'s tuple
param as a scalar.

### M-C — an array-valued kernel *return*

Three distinct behaviours depending on how the return is spelled, and the
first is the bad one.

**M-C1 — dense `method_for`, kernel returns a call: SILENT WRONG ANSWER.**

```blade
let m: Array<Float like Idx<2>, Idx<3>> = [[1.0,2.0,3.0],[4.0,5.0,6.0]]
let fs: Array<Float like Idx<4>> = [1.0, 2.0, 3.0, 4.0]
function spec(r: Array<Float like Idx<3>>, f: Array<Float like Idx<4>>) = {
    (f <@> lambda(x) -> x * prodsum(r, r)) |> compute
}
let grid = method_for(m) <@> lambda(r: Array<Float like Idx<3>>) -> spec(r, fs) |> compute
// compiles, runs, prints:  grid = [[], []]
```

```cpp
static constexpr const size_t grid_extents[2] = { 2 };   // rank 2, ONE extent
Array<double, 2> grid = { allocate<…>(grid_extents), grid_extents };
for (…) grid[__i0] = spec(m____i0, fs);                  // Array assigned into a row slot
```

The outer extent is right, the inner is 0. No diagnostic on any channel. This
is byte-for-byte the failure `tests/corpus/func-arrays/011_fa_t11_rows_of_computed_arrays.blade`
documents for rank-2 literals of computed rows ("emit a short extents table
({2} for a rank-2 array — the inner extent read as 0) … no error anywhere"),
and its fix is the precedent: take the missing inner extents from the declared
or row-inferred array type's trailing `IndexTypes`. Here the source is
`kernelTDims` — which `buildApplyInfo` already computes.

**M-C2 — the same thing written inline: caught, at codegen.**

```blade
let grid = method_for(m) <@> lambda(r: Array<Float like Idx<3>>) ->
    (fs <@> lambda(x) -> x * prodsum(r, r)) |> compute
// grid[__i0] = BLADE_CODEGEN_ERROR_ARRAY-VALUED_ELEMENTWISE_KERNEL_BODY_NOT_SUPPORTED…
```

Note which guard did *not* fire: `arrayValuedComputeBody`
(`TypeCheck.fs:10215`) requires `kernelOutputRank >= 1` **and**
`lambdaInfo.Body.Kind = TExprCompute`. Writing the body as a block
(`-> { (fs <@> …) |> compute }`) or as a call (`-> spec(r, fs)`) both slip past
it — measured: both report `OK` from `blade check`. So **what output rank ≥ 1
can do today is: nothing safely.** `kernelTDims`/`kernelOutputRank`
(`TypeCheck.fs:9902-9908`) are computed and threaded all the way to
`ApplyInfo.KernelOutputRank` (`IR.fs:350`, `Lowering.fs:418`), but no emitter
consumes them to size the output; the only consumer in the tree is
`MLPolyExtractTyped.fs:250`, which uses `<> 0` as a *bail-out* predicate.

**M-C3 — grouped rows: the element type degrades.** `examples/lswosa.blade`'s
`family_spectra` shape, reproduced:

```blade
function fam(ts: T^1, s: U^1, f: Array<Float like Idx<4>>, kk: V^1) = {
    let gk = group_keys(kk)
    let grid = method_for(zip(group_by(ts, gk), group_by(s, gk)))
               <@> lambda(trow: T^1, srow: U^1) -> spec((trow, srow), f) |> compute
    transpose(grid, [0, 1])
}
// error[BL4004]: transpose: axis 1 is out of range for a rank-1 array
// error[BL3001]: Type mismatch: expected Complex128, got Float64   (at `grid <@> mag2`)
```

`grid` comes out **rank 1, element Float64** — the grouped output deduction
takes the *input's* element type and adds no T-dimensions, so both of
`lswosa.blade`'s downstream errors follow mechanically. In C++ the same shape
without the type error emits `out[__g] = spec(out__sub, fs);` with `out`
declared `Array<double,1>` — `cannot convert Array<double,1> to double`.

The explicit **"map before group_by"** refusal (`CodeGen.fs:9911`) is
*orthogonal* and does not fire here: `outputIsRowShaped`
(`CodeGen.fs:9903-9908`) tests whether the result's trailing axes are
themselves ragged/grouped/dep-inner. `lswosa`'s per-segment spectrum is a
**dense** `freqs`-length array, so that guard correctly abstains and the shape
falls through to the standard nest. That refusal stays as-is; it is about
grouped *outputs*, not about array-valued ones.

### M-D — a deferred function return

```blade
function f(xs: Array<Float like Idx<4>>) = { xs <@> lambda(x) -> x * 2.0 }
let out = f(a) |> compute
// return BLADE_CODEGEN_ERROR_UNEVALUATED_COMPUTATION_USED_AS_VALUE_-_USE_|>_COMPUTE;
```

The caller's `compute` does not reach inside. Two further probes settle the
design question outright:

- `function f(xs) = { (xs <@> lambda(x) -> x * 2.0) |> compute }` → **runs**,
  `out = [2, 4, 6, 8]`.
- `let f = reduce(mk(a), (+))` and `(mk(a) <&> mk2(a)) |> compute` — i.e. every
  spelling where a *lazy* return would be useful — **both die with the same
  sentinel**.

And the emitted signature is already `Array<double,1> f(Array<double,1>)`. So
laziness never crosses a named-function boundary today, the ABI already
promises a materialized array, and there is nothing to preserve.

`examples/lsdft.blade` is exactly this: its return position holds the bare
`omegas <@> lambda(w) -> {…}` apply, and the emitted `lsdft(…)` ends
`return BLADE_CODEGEN_ERROR_UNEVALUATED_COMPUTATION_USED_AS_VALUE…`.

---

## 2. The `'__v27'` diagnosis

The plan doc records the lsdft symptom as *"`let e = exp <@> (i * w * ts)` is
re-materialized in the lifted kernel under a fresh id with the `exp` wrapper
lost (`'__v27' was not declared`)"*. Minimal reproduction (ids renumber; the
shape is identical):

```blade
let i = complex(0.0, 1.0)
let ts: Array<Float like Idx<4>> = [0.0, 0.5, 1.0, 1.5]
let ws: Array<Float like Idx<3>> = [1.0, 2.0, 3.0]
let out = ws <@> lambda(w) -> {
    let e = exp <@> (i * w * ts)     // NO |> compute
    prodsum(e, e)
} |> compute
```

emits, inside the lifted kernel:

```cpp
std::complex<double> __lambda_15(std::complex<double> k) { return std::exp(k); }   // never called

std::complex<double> __lambda_17(double w, std::complex<double>& i, Array<double,1>& ts) {
    Array<std::complex<double>,1> __v18 = { allocate<…>(__v18_extents), … };
    for (…) __fp___v18[__fk] = ((i * w) * __fp_ts[__fk]);        // the INNER broadcast only
    auto __retv… = [&]() { … __ps += __v12[__pt] * __v12[__pt]; … }();   // __v12 UNDECLARED
    …
}
```

**It is not a name-map bug in kernel lifting.** It is a *dropped statement*.
Precisely:

1. Lowering builds `IRLet(e, IRApplyCombinator{Kernel = exp;
   Arrays = [IRApp(IRObjectFor …, [ts])]}, …)` — a **bare, unforced**
   combinator, because the user wrote no `|> compute` and nothing inserts one.
2. The IR lift pass hoists the inner `IRApp(IRObjectFor …)` out of the
   combinator's `Arrays` slot into its own let under a **fresh** id (`__v18`).
   That part is correct and is what `liftChildIncludingLoopApp`
   (`IR.fs:6013-6022`) exists for.
3. `genFuncBodyScoped`'s let dispatch then hits this arm:

   ```fsharp
   | IRApplyCombinator _ | IRComposeApply _ ->
       // Unevaluated computations -- deferred until |> compute forces them
       currentNames <- Map.add id varName currentNames
       []                                            // CodeGen.fs:15138-15141
   ```

   **It emits nothing and registers the name anyway.** `__v12` enters
   `currentNames`, so every downstream consumer renders `__v12[…]` against a
   C++ identifier that was never declared — and the `exp` kernel, which lived
   only on the dropped node, is emitted as a free function and never called.

The arm's premise is true at module level, where `genComputeBinding` peels
`IRCompute` recursively and the deferred node is consumed by its forcing site.
It is false inside a kernel body, where `prodsum(e, e)` is a **value** consumer
and there is no forcing site at all.

**Confirmation, and the cheapest fix.** Add the `|> compute` the user did not
write and the *same* build emits perfect code — the let now lands on the arm
immediately below (`CodeGen.fs:15142`, `IRCompute (IRApplyCombinator info)` →
statement-form `genApplyCombinator`):

```cpp
Array<std::complex<double>,1> __v18 = …;
for (…) __fp___v18[__fk] = ((i * w) * __fp_ts[__fk]);
Array<std::complex<double>,1> __v12 = …;
for (…) __fp___v12[__fk] = std::exp(__fp___v18[__fk]);     // wrapper NOT lost
auto __retv… = [&]() { … __ps += __v12[__pt] * __v12[__pt]; … }();
```

So `'__v27'` is one line of policy: **inside a kernel body (and a function
body), a let whose RHS is a bare `IRApplyCombinator` must be forced, not
dropped.** The right place is Lowering's `computeWrap` (`Lowering.fs:514`),
which already owns the dual rule (drop a *redundant* `IRCompute` over an
already-eager `IRApp(IRObjectFor …)`); the CodeGen arm then becomes
unreachable-for-arrays and can be narrowed to a hard error rather than a silent
`[]`.

---

## 3. What already works — and the one decision that is missing

Read together, the probes say the emission machinery is nearly complete and
mis-*dispatched*.

**Works today, verified in emitted C++:**

- `genFuncBodyScoped` (`CodeGen.fs:15083-15223`) has statement arms for
  `IRForRange`, `IRCompute(IRApplyCombinator)`, `IRApp(IRObjectFor …)`,
  `IRArrayLit`, and the whole inline-form family (`IRMask`/`IRSort`/…). That is
  a full statement context.
- Its return arm (`CodeGen.fs:15241-15248`) already synthesizes a `__retN`
  binding for an `IRCompute(IRApplyCombinator)` return, so an **array-returning
  body** works, and the return-extent ABI note at `CodeGen.fs:15266-15279`
  records that heap-allocated extents tables make the wrapper self-describing
  across a call boundary.
- Capture chains are correct at depth. Measured:

  ```blade
  let two = 2.0                              // module level
  function outer(a: …, b: …) = {
      let sc = 3.0                           // function-body let
      b <@> lambda(w) -> {                   // outer kernel param w
          let e = (a <@> lambda(t) -> t * w * sc * two) |> compute
          prodsum(e, e)
      } |> compute
  }
  ```

  emits `double __lambda_17(double t, double& sc, double& two, double& w)` and
  `double __lambda_18(double w, Array<double,1>& a, double& sc, double& two)` —
  the inner kernel correctly captures the outer kernel's param, the outer
  function's param, the function-body let *and* the module-level let, and
  `__lambda_18` materializes `__v14` and prodsums it correctly.
- `genObjectForApplication` (`CodeGen.fs:10830`, `10902`, `10918`) already
  emits loops that **call** a lifted kernel through `genCallableWrapper`
  (`CodeGen.fs:1555-1576`), forwarding captures via `captureForwardName`.

**The missing decision.** The loop emitters that serve `<@> |> compute` —
`genApplyCombinator`'s flat-elementwise fast path (`CodeGen.fs:6640-6745`) and
the nest paths — render the kernel body with `genKernelExprWithReynolds`
(`CodeGen.fs:5700`), whose non-Reynolds branch is literally
`exprToCpp nameMap kernelExpr` (`CodeGen.fs:5750`). One C++ *expression*, no
statement scope. There is no predicate anywhere asking whether the body *can*
be an expression, and no fallback to the wrapper-call form that the sibling
`IRApp(IRObjectFor)` path uses.

That single missing predicate + fallback is the whole of M-A at codegen. Both
halves of the mechanism already exist and are exercised by every other test in
the corpus.

---

## 4. Q1 — Representation

**Options.**

| | shape | cost | risk |
|---|---|---|---|
| (a) per-iteration heap `Array<T,R>` | what the lifted form emits today | 1 malloc+free per outer iteration | none (status quo, already correct) |
| (b) stack buffer (`alloca`/VLA/`std::array`) | needs a compile-time extent | free | only serves literal extents; `Idx<n>` monomorphization already erases many but not all; unbounded stack growth for large rows |
| (c) hoisted buffer, reused across outer iterations | one alloc in the loop preamble | 1 malloc+free per *loop* | needs an invariance proof; interacts with `omp` (per-thread copies) |

**Measured cost of (a).** 200 003 outer iterations, 5-element inner
materialization, ucrt64 g++ 15.2, `-O3 -march=native`, extent deliberately
non-power-of-two (see `memory/stride-layout-facts.md`):

```blade
// perf1: materializing
function inner(w: Float64, tt: Array<Float like Idx<5>>) = {
    let e = (tt <@> lambda(t) -> t * w) |> compute
    prodsum(e, e)
}
// perf2: same value, no materialization
function inner(w: Float64, tt: Array<Float like Idx<5>>) = prodsum(tt, tt) * w * w
```

| | run 1 | run 2 | run 3 |
|---|---|---|---|
| perf1 (per-iteration alloc) | 5.31 ms | 5.23 ms | 5.32 ms |
| perf2 (no alloc) | 0.81 ms | 0.65 ms | 0.64 ms |

**8.1× / ≈ 23 ns per outer iteration.** That is the malloc+free pair, and it is
paid once per *cell of the outer grid*. For `lsdft` at 10⁴ frequencies it is
~0.25 ms of pure allocator — negligible next to the 10⁸ flops of the inner
loops. For a *short* inner axis (the WOSA segment case, tens of samples) it is
the dominant cost.

**Recommendation: (a) as the semantics, (c) as an optimization, never (b).**

- (a) is already implemented, already correct, already scope-owned
  (`registerPoolAlloc` / `popAllocScopeFrees`), and already handles the
  runtime-extent case. It must remain the fallback, because a kernel-local
  extent may depend on the outer param (a ragged row's own length).
- (b) buys little that (c) does not, and a `T^1` row of unknown length would
  need a per-shape decision that the compiler cannot make locally.
- (c) is the perf-critical case named in the brief and is worth doing, but as a
  *separate, provably-guarded* step.

**The hoisting condition, stated precisely.** Hoist the allocation of a
kernel-local materialization to the enclosing loop's preamble iff **all** hold:

1. Every free variable of the buffer's **extent expression** is bound outside
   the enclosing loop — i.e. the extent mentions no loop index and no kernel
   param. In `lsdft`, `e`'s extent is `ts.extents[0]`: `ts` is a
   function-body let, invariant, hoistable. In a grouped/ragged peel, the row
   length is `gk__offsets[__g+1] - gk__offsets[__g]`: **not** hoistable (it
   mentions `__g`) — though the *maximum* row length is, which is a later
   refinement, not a stage-1 target.
2. The buffer does not **escape** the iteration: no path from it to the
   kernel's return value or to an outer-scope write. `computeScopeEscapes`
   (`CodeGen.fs:15379`) already computes exactly this for function bodies and
   is the natural reuse.
3. The enclosing loop is **not** threaded, *or* the hoist is per-thread. A
   single hoisted buffer shared across an `omp parallel for` is a data race.
   The safe stage-1 rule: hoist only when the enclosing level emits serially;
   defer the `firstprivate`/per-thread-slab form. `where omp` on the outer
   kernel is exactly the signal, and it is already on `ApplyInfo`.

Condition 1 is decidable syntactically on the IR extent expression and is the
only new analysis. Conditions 2 and 3 are existing facts read off existing
structures.

---

## 5. Q2 — Layer

`genApplyCombinatorExpr` (`CodeGen.fs:3247-3275`) is worth reading in full
before choosing: its *entire body* is an `exprError`. It resolves array names,
documents at length why a 2-array Cartesian sum-reduce here would be a silent
miscompile for a zip kernel body, and then refuses unconditionally. It is not
an expression-position materializer waiting to be extended — it is a tombstone
marking the place where one was tried. The lesson it records ("no correct
program reaches here") is the argument *against* solving M-A in expression
position.

The recommendation is therefore a three-way split, one manifestation per layer,
with no new layer invented:

**TypeCheck owns shape (M-B, M-C).** An array-valued intermediate must be
array-*typed* before anything downstream can be right, and the demand mechanism
already exists (`requireArrayArgMinRank`, `TypeCheck.fs:2113`). Two additions:

- At the nested-apply and array-binop seams inside a lambda body, when an
  operand resolves to an arity-k infer var, **materialize it** exactly as
  `inferProdSum` does at `TypeCheck.fs:5692-5697`. This is the house-style
  "synthesize into proven machinery" move — the same shape as the reduce-axes
  synthesis and the `complex(a,b)` array lift.
- Feed `kernelTDims` (`TypeCheck.fs:9902-9908`) into the output *extents*, not
  just the output rank. Today they are computed, threaded to
  `ApplyInfo.KernelOutputRank`, and consumed by nobody but an ML bail-out.

Doing this at typecheck rather than lowering is not a preference: the units
second pass, the tag revalidation, and `deduceOutputType` all run *after* the
seam and all read the resolved types, so a shape fixed later is a shape three
other analyses already got wrong.

**Lowering owns forcing (M-A's `__v27` half, M-D).** `computeWrap`
(`Lowering.fs:514-527`) already owns the "is this node eager?" question in the
drop direction. Give it the insert direction: a let-RHS or return-expression
that is a bare `IRApplyCombinator`/`IRComposeApply` **inside a lambda or
function body** gets an `IRCompute`. `compute` is idempotent at inference
(Theme B, 2026-08-08), so a user-written `|> compute` stays a no-op and
`loops/114` is unaffected.

**CodeGen owns exactly one decision: call vs inline.** Add a predicate —
`kernelBodyIsExpressionShaped` — over the kernel's IR body: false if it
contains a materializing node (`IRCompute(IRApplyCombinator)`,
`IRApp(IRObjectFor …)`, `IRReduceCompute`, an inline form, or an
`IRLet` whose RHS is any of those) anywhere other than a blessed slot. When
false, the loop emitter falls back to `genCallableWrapper` + a call, which is
what `genObjectForApplication` already does at `CodeGen.fs:10918`. The lifted
callable is *already emitted* in every case measured — the fix is routing, not
generation.

**Why not statement-position emission inside the loop nest?** It is tempting
(no call overhead, the body's locals could hoist into the nest preamble), and
it should be the eventual form for the hoisted-buffer case. But it duplicates
`genFuncBodyScoped`'s twelve-arm dispatch inside a second emitter with
different indentation, different alloc-scope ownership, and different name
maps — and the `'__v27'` bug is *precisely* what happens when two emitters
disagree about which ids they own. Route to the one that works; revisit
inlining as a `-O` decision once the call form is pinned by value tests. (The
call is not free, but it is one non-inlined call per outer cell against ~23 ns
of allocator on the same path; and g++ inlines a static-linkage callee at
`-O3` when the body is small enough that inlining would have mattered.)

---

## 6. Q3 — Scoping

**What works today.** Nested kernels capture correctly through the whole chain
— outer kernel param, enclosing function param, function-body let, and
module-level let all arrive as capture params in the right order, and
`captureForwardName` (`CodeGen.fs:2167`) forwards them at the call site. §3's
`__lambda_17`/`__lambda_18` pair is the measured proof, at depth 2 with four
distinct capture provenances.

**What breaks, and it is a hard prerequisite.** Module-level `let` bindings are
emitted as **`main()` locals**:

```cpp
int main() { … std::complex<double> i = std::complex<double>(0.0, 1.0); … }   // lsdft.cpp:223
int main() { … double two = 2.0; … }                                          // mS.cpp:160
```

while lifted kernels and user functions are **namespace-scope**. So
`__lambda_51(double w, std::complex<double>& i, …)` — lsdft's frequency kernel,
already emitted correctly — **cannot be called from inside `lsdft(...)`**: the
capture argument `i` has no name in that scope. Today this is invisible,
because the call is never generated. The moment §5's call-don't-inline
fallback fires for a kernel inside a function body that captures a module-level
let, it becomes an undeclared-identifier error.

This is the background item the brief flags as separate (`two_pi` and `i` in
both examples). It is separate in *cause* but it is **stage 0** in *order*: the
central fix cannot be demonstrated on either motivating program without it.

**What the fix must maintain.** Whatever forwards module-level captures (extra
function params threaded from `main`, or promotion of side-effect-free
module-level lets to namespace-scope `const`) must:

1. Preserve `captureForwardName`'s discipline — capture args use the **emitted**
   name from the name map, never the source name
   (`memory/block-local-capture-forwarding.md`). A new forwarding site that
   spells the source name works at module level and breaks in any renamed
   scope.
2. Keep capture *order* stable between the lifted signature and every call
   site, including the wrapper lambda that `genCallableWrapper` synthesizes.
3. Not change what the units second pass sees. `kernelBodyUnits` binds nested
   kernel params to walk-computed operand element units; a capture that changes
   provenance (module let → function param) must keep the same unit signature,
   or `units/052`, `065`, `073` shift.

---

## 7. Q4 — Staging

Each stage is independently landable and independently pinned. The order is
forced by dependency, not preference: S0 unblocks the demonstration, S1
unblocks the type, S2 is the central fix, S3 and S4 are separable.

### S0 — module-capture forwarding + the M-C1 guard *(prerequisite)*

- Forward module-level lets into function-body kernels (the filed background
  item), per §6.
- **Guard M-C1's silent wrong answer now**, independently of S3: when
  `kernelOutputRank >= 1` and the output extents table is short, refuse rather
  than emit `{ 2 }` for a rank-2 table. Broaden `arrayValuedComputeBody`
  (`TypeCheck.fs:10215`) from "body's top node is `TExprCompute`" to "resolved
  return type is an array" so the block-bodied and call-bodied spellings stop
  slipping through.

  *Acceptance:* `mC1` above becomes a diagnostic, not `grid = [[], []]`. New
  pin `diagnostics/0NN_array_valued_kernel_return_rejects`, `ERROR: BL3999`.
  This pin is **deleted** in S3 and replaced by a value test — noted in its own
  header, in the style of `units/065`.

### S1 — the typecheck shape gap (M-B)

Materialize arity-constrained operand vars at the nested-apply and array-binop
seams (§5). Re-check the full discriminating matrix of §1.

*Acceptance pins:*
- `loops/1NN_kernel_body_array_intermediate` — the M-B minimal, as a **value**
  test: `m = [[1,2,3],[4,5,6]]`, `w = r * 2.0`, `prodsum(w,w)` →
  `out = [56, 308]` (4×14, 4×77).
- Same body with `r: Array<Float like Idx<3>>` — must agree, byte for byte.
  (Only reachable after S2; land the pin with S2.)
- `exp <@> (w * ts)` and `exp <@> (i * w * ts)` must now type **identically**;
  today only the complex one does.
- Negative pins unchanged: `unit-errors/004`, `005`, `010`, `011` must still
  reject, and still at **lower**, not codegen.

### S2 — call-don't-inline, and forced lets (M-A, `'__v27'`)

Three edits: `computeWrap`'s insert direction (`Lowering.fs:514`);
`kernelBodyIsExpressionShaped` + the wrapper-call fallback in the loop
emitters; narrow `CodeGen.fs:15138` from silent `[]` to a hard error.

*Acceptance pins:*
- `units/065_nested_map_capture_unit_cancels` — **converted from
  `REJECT-AT: codegen` to a value test**, exactly as its header instructs.
- `units/073_nested_map_letbound_and_capture_units` — likewise; the expected
  values are already written into its header:
  `outA/outB = [1.89188841969345, -0.126920566681172]`.
- `loops/1NN_kernel_body_nested_map` — the M-A minimal as a value test.
- `loops/1NN_kernel_body_nested_capture_depth2` — the §3 depth-2 capture
  program (`two`/`sc`/`w`/`a`), values pinned.
- A **negative**: the emitted `.cpp` for each must contain zero
  `BLADE_CODEGEN_ERROR_` tokens. The WARN-pin harness already captures codegen
  warnings; each removed sentinel needs its `WARN-CODEGEN:` pins removed in the
  same change.

### S3 — output-rank grids (M-C)

Size the output from `kernelTDims`; give the grouped peel's output deduction
the kernel's array return (rank *and* element type). The `func-arrays/011` fix
is the template.

*Acceptance pins:*
- `mC1` as a **value** test; the S0 diagnostic pin is deleted in the same
  change.
- `loops/1NN_grouped_row_array_result` — `mC9`'s shape: `transpose(grid,[0,1])`
  must typecheck (rank 2) and `grid <@> mag2` must see `Complex128`.
- The `outputIsRowShaped` refusal (`CodeGen.fs:9911`) **stays**, and gets its
  own pin proving a genuinely grouped-shaped result is still refused while a
  dense array result is not.

### S4 — return forcing (M-D)

**Recommendation: "the callee forces", not "the caller's compute reaches
inside".** Argument, from §1's probes: the emitted signature is already
`Array<double,1> f(…)`; laziness demonstrably never crosses the boundary
(`reduce(mk(a), …)` and `mk(a) <&> mk2(a)` both die identically); and
`compute` idempotence means a caller-side `|> compute` remains a legal no-op.
Sinking the caller's `compute` into the callee would instead require the
callee's return type to vary by call site — a monomorphization axis with no
payoff, since there is no shape the eager return cannot express.

A pure diagnostic ("add `|> compute` at the return") is the honest fallback if
the implicit force turns out to interact badly with `<&>`/`<&!>` fusion trees;
but the fusion probe above shows those already fail, so nothing regresses.

*Acceptance pins:*
- `functions/0NN_deferred_return_forced` — `f(a) |> compute`, `f(a)` bare, and
  `reduce(f(a), (+))` all give the same values.
- **`examples/lsdft.blade` runs.** Its docstring gives closed-form checks:
  `Z2 = Σ e^{2iωt}`, `ωτ = arg(Z2)/2`, `Σcos² = (n+|Z2|)/2`, `Σsin² =
  (n−|Z2|)/2`; a pure sinusoid at a sampled frequency must return a real
  amplitude with phase referenced to `t_zero`, and `ω = 0` must not be
  singular.
- **`examples/lswosa.blade` runs**, diffed against its dask reference.
- Issue #18 (mixed real/complex `prodsum` over a generic param unifies
  `U := Complex` instead of promoting) is **not** part of this plan but blocks
  the same acceptance; it must land alongside S4 or `lsdft` needs a
  complex-declared driver.

---

## 8. Q5 — Interactions

**Units second pass.** `kernelBodyUnits` was extended (Theme C round 2) with
`TExprApply`/`TExprCompute` arms that recompute a nested map's element
signature recursively (`nestedOperandElemUnits`/`nestedApplyElemUnits`). S1
changes **what those arms see**: today the nested map's operand is often a
scalar-typed var; after S1 it is a real array with a real element type. That is
strictly *more* information and should only remove the residual "operand
subtree has unresolved types" deferrals — but it is exactly the kind of change
that flips a false-accept into a true reject. `unit-errors/004` exists because
of one such false accept; treat `unit-errors/004`, `005`, `010`, `011` as the
tripwire and re-run them at every stage, checking the **stage** of the refusal
(lower, not codegen) and not merely that it refuses. Theme C's two known
residual seams (the `^`/caret rule's premature reject; a lambda called only
directly, never `<@>`-applied) are untouched by this plan and stay open.

**AD.** Not exercised by any probe here. The relevant question is whether the
differentiation pass walks kernel bodies structurally; a body that gains an
`IRCompute` node from S2's `computeWrap` insert changes its shape even though
its value does not. Check before S2 lands rather than after.

**Interp parity.** The interpreter has no path for these shapes:
`Interp/Loops.fs:1478` raises `InterpUnsupported "reduce over a deferred
computation with non-apply leaves"`, and `:1617` raises for standalone
`IRBlocked`. S2 makes previously-unreachable IR reachable, so each new value
pin needs an interp answer or an explicit `InterpUnsupported`. The interpreter
is a byte-for-byte panic twin of the compiled lane
(`memory/blade-frame-cost-mechanism.md`), so a silent divergence here is a real
risk — pin the differential, do not assume it.

**OpenMP.** Two facts, both already in the tree. (i) The flat-elementwise fast
path refuses to fire when `OmpRequested` is set without a full licence
(`CodeGen.fs:6650-6653`) and falls back to the nest — so S2's call fallback
must preserve that fallback order, not short-circuit it. (ii) Inner
materialization loops emit **serial** by default; `lsdft`'s `lambda(w) where
omp(w: 1)` licenses the frequency axis only, and that is the correct reading —
the inner time loop is a per-iteration private buffer, so threading it would
need its own licence. The ragged-peel pragma gap stays open. §4's hoisting
condition 3 is where these two meet: a hoisted buffer under a threaded outer
level must be per-thread or must not hoist.

**Guards removed / narrowed, by stage** — the WARN-pin discipline requires each
removal to drop its pins in the same change:

| Stage | Site | Action |
|---|---|---|
| S0 | `TypeCheck.fs:10215` `arrayValuedComputeBody` | **broaden** (return-type based, not top-node based) — temporarily rejects *more* |
| S1 | — | none removed; `TypeCheck.fs:5736` `prodsum() requires array arguments` stays for genuine non-arrays |
| S2 | `CodeGen.fs:15138-15141` bare `IRApplyCombinator` → `[]` | **replace** silent drop with a hard error (it must become unreachable) |
| S2 | `CodeGen.fs:2178-2186` `exprToCppCore` sentinels | **keep** — they become genuinely unreachable for kernel bodies, but still guard true IR bugs |
| S2 | `CodeGen.fs:3275` `genApplyCombinatorExpr`'s unconditional `exprError` | **keep**, and add a comment recording that the supported route is now the call form |
| S2 | `units/065`, `units/073` `REJECT-AT: codegen` headers | **delete**, convert to value tests |
| S3 | `TypeCheck.fs:10215` `arrayValuedComputeBody` | **remove**; delete the S0 diagnostic pin |
| S3 | `CodeGen.fs:3275` | remove if S3's output sizing makes the 1-array inline combinator reachable and correct; otherwise keep |
| S3 | `CodeGen.fs:9911` "map before group_by" | **keep** — orthogonal (grouped-shaped outputs, not array-shaped) |
| S4 | `CodeGen.fs:2181` `UNEVALUATED_COMPUTATION_USED_AS_VALUE` at function-return position | becomes unreachable; keep the sentinel, add a return-position pin |
| — | `loops/086_zip_rank2_elementwise_body_rejects` (BL3999) | **re-evaluate at S3.** Its refusal exists because the inline path collapsed `ra * rb` to `(Σra)(Σrb)` — a silent miscompile. With materialization the row product is expressible; if the reject is lifted, pin the *values* (`[[10,40,90],[160,250,360]]`), not just the absence of the error. |

---

## 9. Q6 — Cost

**Corpus blast radius is small.** Of 1473 `.blade` files under
`tests/corpus/`, **19** have a brace-block kernel body at all, and of those 15
contain a nested `<@>`. The affected set is essentially:

| File | Today | After |
|---|---|---|
| `units/065_nested_map_capture_unit_cancels` | `REJECT-AT: codegen` | **value test** (S2) — its header already says "turn it into a value test when #3 lands" |
| `units/073_nested_map_letbound_and_capture_units` | `REJECT-AT: codegen` | **value test** (S2); expected values already in the header: `[1.89188841969345, -0.126920566681172]` |
| `unit-errors/004`, `005`, `010`, `011` | reject at lower | **unchanged** — these are the tripwire |
| `units/074_scalar_unit_cancellation_values` | value test | unchanged; it is 065/073's top-level twin |
| `loops/086_zip_rank2_elementwise_body_rejects` | BL3999 | re-evaluate at S3 (above) |
| `sql-group-by/020_groupby_elementwise_rejects` | `REJECT-AT: codegen` | **unchanged** — different guard (`CodeGen.fs:9911`'s neighbour), grouped outputs |
| `func-arrays/011_fa_t11_rows_of_computed_arrays` | value test | unchanged; it is S3's precedent, and a good regression sentinel for the extents-table fix |
| `loops/114_compute_elementwise_in_function_body` | value test | unchanged; guards `computeWrap` idempotence, which S2 edits |
| `functions/055_prodsum_reduce_broadcast_operand` | value test | unchanged; guards `liftChildIncludingLoopApp`, which S2 leans on |

The other `REJECT-AT:` pins (`index-types/025`, `loops/065`, `reynolds/014`)
are unrelated.

**Where the real risk is** — not in the pins that flip, but in the shared paths
each stage touches:

- S1 edits type inference at the array-binop seam, which is on the path of
  *every* elementwise expression in the corpus. A before/after `blade check`
  sweep over all 1473 files (the §9 method from `plan-tuples-vs-arg-packs.md`,
  which found "one file changed outcome") is mandatory, not optional.
- S2 edits `computeWrap` and adds a fallback in the loop emitters — the flat
  elementwise path is the hot path for essentially every `<@> |> compute` in
  the tree. The predicate must be conservative in the *inline* direction: a
  false "not expression-shaped" costs a call; a false "expression-shaped"
  reintroduces a sentinel.
- S3 edits output deduction, which every apply reads.

**Wall-clock estimate, honest.** These are ranges, and the wide ones are wide
because the sweep-and-diff cycle dominates the edit.

| Stage | Estimate | Dominated by |
|---|---|---|
| S0 module-capture forwarding | 1–2 days | choosing between param-threading and namespace promotion; the latter needs a purity judgement on module-level lets |
| S0 M-C1 guard | 0.5 day | mostly the new pin + checking nothing else was relying on the loose guard |
| S1 typecheck shape | 1–2 days | the corpus `check` sweep and reading its diff, not the edit |
| S2 call-don't-inline + forced lets | 2–4 days | the predicate's conservatism, the omp fallback ordering, and interp parity for newly-reachable IR |
| S3 output-rank grids | 2–3 days | the grouped peel; the dense case is `func-arrays/011` again |
| S4 return forcing | 0.5–1 day | trivial edit; the time is in the two examples' numerics |
| Both examples to *correct values* | 2–3 days | issue #18 (mixed real/complex prodsum), the `lswosa` reference diff, and the unit ascriptions |

Total ≈ **9–16 days**, with S2 the single hardest and S0 the one that must go
first regardless of how the rest is scheduled.

---

## 10. Acceptance

1. `examples/lsdft.blade` compiles, runs, and passes the closed-form checks its
   own docstring states (§7, S4).
2. `examples/lswosa.blade` compiles, runs, and matches its dask reference,
   including the two deliberately-preserved reference quirks (off-centre taper;
   segment bounds on `[0, t_max − t_min]` compared against unshifted `ts`).
3. The four minimals of §1 land as **value** tests, not reject probes:
   M-A (nested loop-object map), M-B (array-valued intermediate under a `T^1`
   row param), M-C (array-valued kernel return, dense *and* grouped), M-D
   (deferred function return).
4. `units/065` and `units/073` are value tests with the values their headers
   already record.
5. Zero `BLADE_CODEGEN_ERROR_` tokens in the emitted C++ for any of the above,
   and `unit-errors/004`/`005`/`010`/`011` still reject **at lower**.
6. The corpus `blade check` sweep diff is explained file by file — every
   outcome change is either a pin this plan says flips, or a bug.
