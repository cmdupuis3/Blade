# Reduction joins

Ruled 2026-08-11. `<&!>` is Blade's **declared** join surface: loop joins are
written down, never guessed. It already joined `<@>` maps —

```blade
let s1, s2 = reduce((L <@> k1) <&!> (L <@> k2), (+))
```

— one loop, two accumulators, verified in `tests/corpus/loops/060`. The gap this
document closes: the **reduction primitives** were not valid legs. You could
join two maps under a shared fold, but you could not join
`prodsum(s, ct)` with `prodsum(ct, ct)`, which is the shape every
sufficient-statistic sweep and every per-frequency spectral kernel is made of.

Two spellings are implemented, both ruled by the owner.

## Form 1 — the pack join

```blade
let anum, adenom, bnum, bdenom =
    object_for(<&!>) <@> (prodsum(s, cwt), prodsum(cwt, cwt), prodsum(s, swt), prodsum(swt, swt))
```

`object_for(<&!>)` is the fusion operator as the kernel of a loop former —
operator sections have always been legal kernels, so this is the house idiom,
not new syntax. Applied over a **pack** of reductions it joins them into ONE
traversal answering a `Tuple<k>`.

## Form 2 — the array fold

```blade
let prods = [prodsum(s, cwt), prodsum(cwt, cwt), prodsum(s, swt), prodsum(swt, swt)]
let anum, adenom, bnum, bdenom = reduce(prods, (<&!>))
```

The `(<&!>)` section in the **fold** position: the fold is the associative join
chain `leg1 <&!> leg2 <&!> …`, so the operand is a list of legs. Written inline
(`reduce([r1, r2], (<&!>))`) or bound to a name by a literal; both reach the
same node as Form 1.

---

## 1. Design decisions

### 1.1 Both forms elaborate to ONE node — the existing fused terminal

A join is normalized, leg by leg, into the `(traversal, fold kernel, seed)`
triple that `reduce`-over-a-deferred-computation is already made of:

| leg | traversal leaf | fold | seed |
|---|---|---|---|
| `prodsum(x1..xk)` | `method_for(zip(x1..xk)) <@> lambda(p1..pk) -> p1*..*pk` | `(+)` | `0` |
| `reduce(<deferred map>, op, i)` | the map itself | `op` | `i` |
| `reduce(<array>, op, i)` | `method_for(A) <@> lambda(p) -> p` | `op` | `i` |

so the k legs become a left-nested `TExprFusion` tree over resolved applies —
byte-for-byte the shape the `<&!>` chain already produces — and the whole join
is `TExprReduce(tree, kernels, seeds)`, lowered to `IRReduceCompute`. No new IR
variant, so every generic walker (`ExprShape`, `liftExpr`, `checkScope`, the
lift/monomorphize passes, the interpreter's deferral) keeps working untouched.

**Why normalize instead of adding an IR node.** The alternative was
`IRReduceJoin of comps * kernels * seeds`, which would have needed ~15 walker
sites updated by hand (`childrenOf`, `typeOf`, `liftExpr`, `isStatementShaped`,
`checkScope`, the pretty-printer, nine CodeGen catch-alls listing deferred
forms, two interpreter arms) — every one of them a chance to miss and get a
silent drop rather than a compile error. Normalization spends nothing there.

### 1.2 The join encoding: per-leg kernels and seeds

`IRReduceCompute(comp, kernel, init)` gains one convention: for a join,
`kernel` and `init` are each an `IRTuple` of k entries in leaf order.

**Per-leg SEEDS are what force this, not heterogeneous operators.** Even an
all-`(+)` join has k distinct seeds — `prodsum` seeds at 0 while
`reduce(x, (+), 10.0)` seeds at 10.0 — so a shared seed slot could not express
the feature at any width. Once seeds are per-leg, per-leg kernels are nearly
free, and they buy the honest generalization: `prodsum` joined with
`reduce(b, max, -inf)` and `reduce(a, (*), 1.0)` in one pass
(`tests/corpus/loops/144`).

A single (non-tuple) kernel/init stays the shared-fold form the `<&!>` chain has
always used. Exactly three places read the distinction: `typeOf`, the fold
emitter, and the interpreter's fold.

### 1.3 A join answers a FLAT `Tuple<k>`

`<&!>` between two maps is a *binary* operator, so the chain's result type
nests: `((e, e), e)`. A join is *k-ary* by construction and its emitted value is
one flat `std::make_tuple` of k accumulators. Typing it nested over a flat value
is what makes a nested `std::get` chain project the wrong slot — and, in a
kernel body (where the destructure does not resolve through `TupleChildren`),
not compile at all. **So a join types flat**, and every projection is `get<i>`.

This is why the 4-leg kernel-body case works here without waiting on the
separate in-flight fix for `>=3`-leg fusion-tree reduces in kernel bodies: that
bug is the nested-type-over-flat-value mismatch, and a join does not have it.
The `<&!>` CHAIN still does — untouched, deliberately, since it is another
session's fix.

### 1.4 Sharing by naming

> legs referencing the SAME named deferred map evaluate that node ONCE per
> joint iteration

```blade
let ct  = ts <@> lambda(t) -> cos(w * t)     // NO compute -- deferred
let stt = ts <@> lambda(t) -> sin(w * t)
let pc, ps, qcc, qcs =
    object_for(<&!>) <@> (prodsum(s, ct), prodsum(s, stt), prodsum(ct, ct), prodsum(ct, stt))
```

emits

```cpp
for (size_t __i0 = 0; __i0 < ts.extents[0]; __i0++) {
    double ts____i0 = ts[__i0];
    double s____i0 = s[__i0];
    const double __v66 = std::cos((__v63 * ts____i0));
    const double __v69 = std::sin((__v63 * ts____i0));
    __v86_0 = w0(__v86_0, (s____i0 * __v66));
    __v86_1 = w1(__v86_1, (s____i0 * __v69));
    __v86_2 = w2(__v86_2, (__v66 * __v66));
    __v86_3 = w3(__v86_3, (__v66 * __v69));
}
```

**The declaration is the NAME.** Nothing is deduced about what the legs compute
and no expression is compared to any other; the legs spell the same binding, so
the binding is bound once per cell. A `|> compute`d operand is an array and
keeps today's behavior (read from memory). This is the difference between a
language feature and compiler CSE, and it is why the `let` with no `compute` is
load-bearing rather than stylistic.

Mechanically it is three small moves, all local to the join emitter:

1. Each distinct deferred operand becomes an extra **share leaf** of the merged
   nest, ordered before its consumers, whose `ShareDecl` makes it emit
   `const T <name> = <kernel>;` instead of a cell write or an accumulate.
   The name is the deferred binding's own emitted name.
2. A consumer leg's deferred operand slot is **repointed** at the deferred
   map's leading source array, so the level's extent and peel name exist in C++
   — and dedup with the share leaf's identical peel, so `ts` is read once.
3. The kernel param that slot bound is **substituted** for a reference to the
   deferred binding, which renders through `ctx.VarNames` as exactly the shared
   local's name.

Nothing else in the nest emitter needs to know a share leaf exists.

**Inside a kernel body** this needs one more thing. Lowering's S2
(`forceBareCombinatorLets`) wraps every body-local `let` whose RHS is a bare
combinator in `IRCompute`, because a body has no forcing site downstream — so
`ct` would be materialized into a whole array per outer cell before the join
ever saw it. S2 now exempts an id that is read **only** as a join operand
(`joinDeferrableIds`). The "only" is what makes the exemption safe rather than a
guess: an id with any other consumer still wants an array, and S2's rule still
holds for it. The reference accounting counts both the `Arrays` slot and the
`Loop` provenance that repeats it (`method_for(zip(s, ct))` names `ct` twice) —
an accounting that saw one of the two could never balance, and the first
implementation silently never fired for exactly that reason.

### 1.5 The joint index space

Every leg must fold the same cell grid: equal rank, and equal extents wherever
both are statically known. Only a **provable** disagreement is an error;
unknown extents are trusted and the nest takes its bound from the leading leg —
the rule `prodsum` has always applied to its own operands, lifted one level up
to the join's legs. Refused with a `BL3999` naming the axis and both extents
(`tests/corpus/loops/146`).

Codegen carries a backstop, `checkJoinCompatible`, which is
`checkMergeCompatible` with one relaxation: two legs may take their extent from
**different arrays**. `prodsum(s, ct)` walks `s` while `prodsum(ct, ct)` walks
`ct`'s source, so requiring one array to name every level's extent would refuse
the shape the feature exists for.

### 1.6 The 1-leg ruling: identity

**One leg is the leg itself** — a scalar, not a `Tuple<1>` (Blade has no
1-tuple), in both spellings. Three reasons, all pointing the same way: it is the
right unit for an associative join chain; it keeps
`reduce(<literal>, (<&!>))` total over any non-empty leg list; and it routes
through the SINGLE-leaf fused terminal, so a one-leg join keeps every
specialization the multi-leg nest declines (BLAS dot dispatch, the chunked `omp`
fold, the reassociated lane forms). It is also what the existing terminal
already does with a one-leaf tree (`[one] -> one`), so the two agree.

**Zero legs is refused.** An empty join names neither an index space nor an
element type. It has no spelling in Form 1 (`<@> ()` cannot be written); the
refusal exists for a generated or edited-down leg list.

### 1.7 Form 2's elaboration strategy, and its one restriction

Form 2 is a **surface pattern**, as ruled: `[leg, …]` plus `reduce(…, (<&!>))`
elaborates to the same join node as Form 1. Computations are NOT first-class
array elements and nothing here moves toward that.

Inline (`reduce([r1, r2], (<&!>))`) needs nothing — the legs are right there in
the AST. The **named** spelling needs the surface elements back after the
literal has been typed into k independent scalars, so `inferLetBindingValue`
records them in `TypeEnv.JoinLegLists`, keyed by name. That is the same
name-keyed surface side channel `FuncDefaults` already is, with the same known
shadowing weakness and one extra guard: the join re-validates that the leading
element is leg-shaped, so `reduce(<data array>, (<&!>))` gets its own steering
diagnostic instead of treating `1.0` as a leg.

**Restriction, documented rather than papered over:** the named leg-list binding
keeps its ordinary meaning and is *also* materialized as an eager array. In
`tests/corpus/loops/139`, `legs = [70.0, 90.0]` prints alongside the join's
results. That is a redundant computation, not a wrong one — and in a
differential test it is a fifth independent route to the same numbers. Use the
inline form or Form 1 where the redundancy matters. Suppressing the binding
would require either a pre-scan rewrite of the statement list or a lowering-side
suppression set, both of which buy less than they cost while `object_for(<&!>)`
exists.

---

## 2. Edits

| file:line | what |
|---|---|
| `src/Parser.fs:2803` | `combinatorOrScalarSection` — ONE section table for `(op)` and `object_for(op)`, so `(<&!>)` parses (it did not: `BL1999 Unknown operator in section`) |
| `src/Parser.fs:2733,2775` | both section sites read that table |
| `src/TypeEnv.fs:295,336` | `JoinLegLists` side channel + its `emptyEnv` entry |
| `src/TypeCheck.fs:12794` | record an array-literal `let` RHS into `JoinLegLists` |
| `src/TypeCheck.fs:5712` | `joinLegSurface` — one leg's normalization |
| `src/TypeCheck.fs:5761` | `isJoinLegShape` — the dispatch predicate that keeps map fusion and reduction joins apart |
| `src/TypeCheck.fs:5776` | `joinLegListOf` — Form 2's leg list |
| `src/TypeCheck.fs:5787` | `inferReductionJoin` — the join proper |
| `src/TypeCheck.fs:5384` | Form 2 intercept at the `ExprReduce` dispatch |
| `src/TypeCheck.fs:8077` | Form 1 intercept at the head of `inferBinOp` |
| `src/IR.fs:129` | the join encoding, documented on `IRReduceCompute` |
| `src/IR.fs:2573` | `LoopNestCodeGen.ShareDecl` |
| `src/IR.fs:3750` | its `None` default at the one construction site |
| `src/IR.fs:5922` | `typeOf` — flat `Tuple<k>` for a join |
| `src/Lowering.fs:281` | `joinDeferrableIdsMany` + `forceBareCombinatorLetsExcept`: S2's exemption |
| `src/CodeGen.fs:12167` | `checkJoinCompatible` |
| `src/CodeGen.fs:12396` | the fused nest's `ShareDecl` arm |
| `src/CodeGen.fs:15875` | `genReduceComputeBindingCore` routes the join encoding out |
| `src/CodeGen.fs:16338` | `genReduceJoinCore` — the join emitter |
| `src/CodeGen.fs:17098,17105` | `currentDeferred` + `joinDeferredIds` in `genFuncBodyScoped` |
| `src/CodeGen.fs:17210` | the body-let arm that keeps a join operand deferred |
| `src/Interp/Loops.fs:1541` | per-leg folds and seeds, and the flat join tuple, in `forceReduceCompute` |

The interpreter needs **no** sharing machinery: sharing a named deferred map is
a per-iteration CSE of a pure map, so the values are identical either way and
each leg keeps its own nest.

---

## 3. Corpus

`tests/corpus/loops/139`–`149`. The differential ones compute the same
statistics by every available route and pin them EQUAL.

| test | what |
|---|---|
| `139_reduction_join_two_legs` | 2 legs, top level, four ways (pack / array fold / infix chain / separate eager) |
| `140_reduction_join_four_legs` | 4 legs, top level, four ways, on values with no exact binary form; residuals pinned `0.0` |
| `141_reduction_join_shared_deferred` | shared named deferred `ct`/`st`, 4 legs, vs the materialized-array spelling |
| `142_reduction_join_kernel_body` | 2 legs inside a kernel body, vs eager |
| `143_reduction_join_wosa_kernel` | the wosa shape: 4 accumulators, shared `cos`/`sin`, in a kernel body, vs eager |
| `144_reduction_join_heterogeneous` | three different fold kernels and four different seeds in one pass |
| `145_reduction_join_one_leg` | the identity ruling, both spellings |
| `146_…_extent_mismatch_rejects` | legs over different index spaces |
| `147_…_non_reduction_leg_rejects` | a non-reduction in a leg slot |
| `148_…_leg_needs_init_rejects` | a lambda fold kernel with no seed |
| `149_…_fold_not_leglist_rejects` | `reduce(<data array>, (<&!>))` |

### What the differential testing caught

1. **The pack spelling silently computed nothing.** On master
   `object_for(<&!>) <@> (prodsum(a, b), prodsum(b, b))` typechecked `OK`, ran,
   and simply did not bind `x` or `y` — no diagnostic, no output. It is now
   either a join or a refusal (`147`).
2. **The flat/nested tuple mismatch.** The first implementation typed the join
   nested, like the chain. At top level the destructure resolves through
   `TupleChildren` and hid it; in a kernel body it emitted
   `std::get<0>(std::get<0>(std::get<0>(t)))` against a flat 4-tuple and did not
   compile. Found by moving `140`'s shape into a kernel body — §1.3.
3. **Sharing silently did not fire in kernel bodies.** `142`/`143` compiled and
   were numerically right while emitting two materialized arrays per outer
   cell — the exemption's reference accounting missed the `Loop` provenance
   copy of each operand, so the count never balanced. Caught by reading the
   emitted C++ for a test that was already green, which is why the acceptance
   criterion is textual (one `cos`, one `sin`) and not just numeric.
4. **The pack spelling was already taken.** `object_for(<&!>) <@> (c1, c2, c3)`
   over deferred MAPS is n-ary map fusion answering three arrays
   (`tests/corpus/loops/029`), and the first Form-1 intercept hijacked it —
   caught by the before/after corpus sweep, as the single verdict change outside
   the new tests. The legs now decide which reading applies (§1.3 of the
   dispatch, `isJoinLegShape`).
5. **The interpreter assembled the wrong tuple shape.** `forceReduceCompute`
   built nested `VTuple` pairs from the fusion tree while the join's type is
   flat, so every projection past index 1 died with `BL8003 tuple projection
   index out of range` — invisible at 2 legs (a 2-pair IS flat) and fatal at 3.
   Found by exercising the join through the REPL, which is the interpreter lane.
6. **FMA contraction is the one thing a join does not preserve bit-for-bit.**
   `143`'s residual against the eager spelling is `-3.55e-15`, not `0`. Under
   `BLADE_FP_CONTRACT=off` it is exactly `0` — verified — which identifies the
   cause: `prodsum`'s `__ps += a[t]*b[t]` and the join's `acc = w(acc, a*b)`
   present g++ with different contraction opportunities. Summation ORDER is
   never affected; a join shares the traversal, it does not reorder the fold.
   `139`–`142` and `144` are exactly `0.0`.

---

## 4. Performance: `wosa_v6`

`C:\bt_bench\wosa_v6.blade` is `wosa_v2` with the frequency kernel's four
`prodsum`s respelled as a Form-1 join over shared deferred `ct`/`stt`. The
emitted inner loop is the one in §1.4: one `cos`, one `sin`, four accumulators,
no per-frequency temporaries, where `v2` materializes two arrays per frequency
and makes four passes over them.

Best-of-5, `bench.ps1`, internal `completed in`, three paired runs on an
otherwise idle machine (`v2`'s numbers in the third run reproduce the
0.191 / 0.0285 reference, so that run's absolutes are directly comparable):

| | serial | 16T |
|---|---|---|
| `wosa_v2` | 0.2064 / 0.1980 / 0.1926 | 0.0349 / 0.0339 / 0.0307 |
| `wosa_v6` | **0.1858 / 0.1781 / 0.1761** | **0.0329 / 0.0333 / 0.0286** |

**≈ 9 % faster serial and ≈ 7 % faster at 16T, in every paired run.** Against the
external references: `v6` at 0.176 / 0.0286 is now at or just past hand-written
identity-C (0.179 / 0.027) serially, and within ~6 % of it threaded; classic C
is 0.34 / 0.05.

Numerics are unchanged: `wosa_v6_bins` reproduces `wosa_v2_bins` bit-for-bit on
6 of 7 probe bins and to 2.3e-14 relative on the seventh (a near-zero imaginary
bin), and lands in the same +0.090 % … +0.127 % band against `ref_bins.exe` on
the four power bins.

The gain is smaller than the removed work suggests, and the reason is worth
recording: `v2`'s two materialization passes are flat elementwise and
*vectorizable* (`BLADE_IVDEP`), while a four-accumulator fused fold is a scalar
dependence chain. A join trades memory traffic and array temporaries for vector
width. When the traffic is small and the body is transcendental, that trade is
worth about 10 % rather than the 4x the pass count implies — and a fold-lane or
`omp simd` form for multi-accumulator joins (the single-leaf terminal already
has both, §5) is where the rest of it is.

---

## 5. Not done

* **Compact (symmetric/antisymmetric/Hermitian) leg storage** — refused, with
  the same message and for the same reason `reduce` and `prodsum` refuse it:
  folding canonical vs logical cells differ.
* **Ragged / grouped / compound leg inputs** — refused at codegen.
* **`omp` / `cuda` / `mpi` on a join** — refused: joined accumulators are shared
  scalars. The single-leaf terminal's chunked `omp` fold (Path B) is untouched
  and a 1-leg join still reaches it.
* **Staggered-rank legs.** The merged nest supports them; a join requires equal
  rank, because "the same joint index space" is what the surface declares.
* **Suppressing Form 2's eager leg-list binding** — §1.7.
* **A shared deferred map with several source arrays** is handled (the leading
  source supplies the bound; the share leaf peels all of them), but is only
  exercised at one array by the corpus.
