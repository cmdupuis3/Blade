# P2: consumer fusion for windowed recursive arrays

> **Status (2026-10-11): BUILT ON AN UNMERGED BRANCH, NOT ON MASTER.** The code lives on
> `claude/recursive-array-materialization-381708` (2026-08-10, kept as the reference
> implementation). It predates the August file split (TypeCheck/CodeGen/IR groups), the
> reassignment of BL4017 (now "symmetry clause licenses nothing here"), the `while` guard
> (freeze + BL8010), reverse-mode AD through recurrences (which uses the full buffer as its
> tape), the freeze-idiom optimizer pass and `BLADE_PRINT`. A port is a re-implementation
> guided by this design: a windowing classifier must demote differentiated and
> `while`-guarded `let rec`s, mint a fresh diagnostic code, and replay prints under print
> selection. The "IMPLEMENTED" status below is the branch's, and its `file:line` cites are
> against the pre-split tree.

**Status: IMPLEMENTED — 2026-08-10.** Implements section 9 of
`docs/plans/plan-rec-array-windowing.md` (v2a fold fusion, v2b elementwise-compute
fusion), on top of P1's ring/snapshot windowing. Section 9b (provider write
terminals) is explicitly NOT in scope: a provider write of the binding stays a
demoting bare use.

Contents: the reduce route map (§1), the decision function
(`src/RecFusion.fs`, §2), the splice (§3), the corpus (§4), what shipped and
what did not (§5), and the verification record (§6).

**What fusion buys.** The sole consumer's per-slice work rides the sweep, so
the trajectory never materializes. Measured on `030`: a 120x4 trajectory
becomes a `{2, 4}` ring plus the 120-element output vector — 480 doubles down
to 128, and the ratio grows linearly with N (the ring is fixed at K+1 slices).
For the nested collapse (`031`) nothing but a scalar survives.

**The one invariant to hold on to.** `Fusable ⇒ R = ∅`. A snapshot read is
itself an occurrence of NAME, so any static read makes the fusion classifier
answer `multiple-uses`. Fusion and snapshots can never coexist, which is why
the splice never has to order a fused write against `snapCopyStmts`. Corpus
`036` pins it.

---

## 1. The reduce route map

`reduce(A, op[, init][, axes = n])` parses to
`ExprReduce(array, kernel, init, axes)` (Parser.fs:2281-2323; six accepted
spellings, including the kernel-omitted `reduce(A)` and `reduce(A, axes = n)`
which both default the kernel to a `(+)` section). `axes` must be an integer
literal — the result RANK is `rank(A) - n`, a static property.

`inferReduce` (TypeCheck.fs:5577) then picks between four routes, in this
order. Let `r` = the operand's rank and `n` = the axis count (default 1).

| # | gate | site | what it produces |
|---|---|---|---|
| R1 | `r >= 2 && n < r` | dispatch :5835, body `partialFold` :5683 | **REWRITES to the row-mode apply**: `(method_for(A) <@> lambda(__pfrowK) -> reduce(__pfrowK, op[, init][, axes = n])) |> compute`. No new IR node, no new loop, no new codegen. Result rank `r - n`, leading axis preserved. |
| R2 | `r >= 2 && n = r`, dense/plain axes, static extents, scalar element, not a deferred-with-`omp` operand | dispatch :5938, body `rankKDesugar` :5848 | An internal block: `let __rksrcU = A; let mut __rkaccU = seed;` + a `List.foldBack2` loop nest over every axis, body `__rkacc = op(__rkacc, __rksrc(i0, .., ik))`. Declared row-major order, ONE scalar accumulator. Seed = the 3-arg init, or the `(+)`/`(*)` identity; **no seed ⇒ hard error** (:5913). |
| R3 | operand is a DEFERRED computation (`A <@> f`, or an `<&!>` fusion tree) | :5960 `tryInferReduceCompute` | `TExprReduceCompute` — the fused reduction terminal, no intermediate array. Not reachable from a `let rec` binding, which is always a materialized array. |
| R4 | `r = 1` | :6015-6065 | `TExprReduce(tArr, tKernel, tInitOpt)`, typed at the element type. This is the base route. |

Everything else is an error: a wreath axis (:6012), a compact rank-1 record
(:6015), and the "full fold the rank-k nest declined" arm (:6066).

`operandRank` (:5606) is what decides `r`: an array's `IndexTypes.Length`, or —
for a single non-compose apply leaf — the deferred computation's output rank.
An `<&!>` fusion TREE deliberately answers `None`, so it always lands on R3.

### Which route each shape over a rank-2+ `let rec` binding takes

With `traj : Array<T like Idx<N>, W>` (r = 2):

| source | route | result |
|---|---|---|
| `reduce(traj, op)` | **R1** with n = 1 | rank-1, `Idx<N>`: one scalar per step — the loss trace |
| `reduce(traj, op, init)` | **R1** with n = 1 | same, each row's fold seeded by `init` |
| `reduce(traj, op, axes = 2)` | **R2** | one scalar over every cell |
| `reduce(reduce(traj, op), op)` | inner **R1**, outer **R4** | inner N-vector materializes, outer folds it to a scalar |
| `reduce(traj, op, axes = 1)` | **R1** (identical to the default) | rank-1 |

With `errs : Array<T like Idx<N>>` (r = 1) — the `ml-e2e` shape:

| source | route | result |
|---|---|---|
| `reduce(errs, op)` | **R4** | scalar |
| `reduce(errs, op, init)` | **R4** | scalar, seeded |

Rank 3 adds `reduce(traj, op, axes = 2)` → R1 with n = 2, result rank 1.

`RecFusion.routeFor` is this table as a pure function, unit-pinned in
`tests/Test_RecFusion.fs`.

### Fold ORDER and SEED, exactly (what the splice must reproduce)

* **R4 seedless** — seed = element 0, fold `i = 1 .. n-1`. Empty operand
  panics `BL8003` (CodeGen.fs:3451 expression form / :14662 binding form;
  `ArrayOps.reduceArray`, ArrayOps.fs:748, is the interpreter twin). A
  statically empty operand is rejected at typecheck instead (:6033).
* **R4 with init** — seed = init, fold ALL elements from 0. The empty fold is
  defined (it is init), so no guard is emitted for any extent.
* **R2** — never seeds-with-first: a rank-k nest cannot do it without a
  per-element guard, so it demands an init or an identity-bearing section.
  Order is declared row-major over the whole grid, ONE accumulator.
* **R1** — inherits R4's rules per ROW; the outer iteration is the proven
  `<@>` former.

### The `rankKDesugar` omp guard, and why it must stay bypassed

`rankKDesugar` declines (:5862-5876) for a DEFERRED rank-k operand whose fold
kernel carries `where ... omp`: the desugar would materialize the computation
and fold it with a hand-built nest carrying no clause, i.e. a silent
serialization — the BL9001 failure mode.

R1 is dispatched FIRST, and with the default `n = 1 < r` it fires for every
rank-2+ operand, so the omp-decline is unreachable for the default spelling.
That is not a bug and must not be "fixed": R1's synthesized body is itself a
`reduce`, so `checkFoldOmpLicense` (:6132, reached at :5948) still runs on the
same kernel at the inner fold. Nothing is dropped silently; only the
materialization-avoidance decline is skipped, and R1 does not build the
temporary that decline existed to avoid.

**Consequence for P2**: the fused splice emits a bare serial accumulation with
no clause of its own, so it has the exact failure shape the guard forbids. A
fold kernel that asks for `omp` must therefore **decline fusion** and fall back
to the ordinary route (obligation `KernelDoesNotAskOmp`, §3.6).

---

## 2. The decision function

`src/RecFusion.fs`, `classify : name -> rank -> extent -> tail -> final ->
FusionVerdict`. Depends on `Blade.Ast` alone (no `TypeEnv`), so the whole
grammar is unit-testable in process: `blade test rec-fusion`, 30 cases,
`tests/Test_RecFusion.fs`.

The four rules, in enforcement order, are documented in the module header. The
two that carry the soundness argument:

**Textual single use.** `countVarOcc` is a NEW exhaustive walker, not a reuse,
because both existing counters are unsound for this job:

* `Unfold.mapExprPre` (Unfold.fs:56) does not descend `ExprHalo`'s offsets
  expression, `ExprAlign`'s `BndPad` expression, or a `LambdaParam.Default`.
* `TypeCheck.collectFreeVars` (TypeCheck.fs:1463) ends in `| _ -> Set.empty`
  (:1574), which silently returns ZERO free variables for `ExprTranspose`,
  `ExprDecompact`, `ExprGram`, `ExprBlocked`, `ExprPartialApp`, `ExprStatic`
  and `ExprRecArray` — **precisely the nodes whose presence has to force a
  demotion**. Pinned as a test: `countVarOcc` answers 1/2/1 for
  `transpose(A, [0,1])` / `gram(A, A)` / `decompact(A, 0)`.

  *(That is also a REACHABLE capture-analysis defect in `collectFreeVars`
  itself, confirmed 2026-08-10: a lambda that reaches a BLOCK-LOCAL array only
  through `transpose` never records it as a capture, and the program dies with
  `BL6001 [IR Validation] dangling VarId reference` instead of compiling.
  Reproducer:*

  ```blade
  let out = {
      let L: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
      let k = lambda(i: Int) -> transpose(L, [0, 1])(i, 0)
      k(0) + k(1)
  }
  ```

  *Loud, so a defect rather than a soundness hole; module-level bindings are
  unaffected (they lower as globals, not captures). Out of scope here; flagged
  separately.)*

`countVarOcc` has no wildcard arm, so FS0025 audits future AST growth.
Shadowing is deliberately ignored: over-counting costs a demotion,
under-counting would fuse a binding that is still read.

**Whitelisted consumer, not blacklisted rejection.** `NAME` must occupy a
recognized operand slot. Everything else — `transpose`, `sort`, `gram`,
`decompact`, an fft call, a bare alias, a dynamic index, an assignment target,
a provider write — simply fails to match and demotes.

### Shapes recognized

| shape | source | splice |
|---|---|---|
| `FuseFold (Rank1Full, k, i)` | `reduce(NAME, k[, i])`, r = 1 | §3.1 |
| `FuseFold (PartialRowFold (n, _), k, i)` | `reduce(NAME, k[, i][, axes = n])`, r >= 2, n < r | §3.2 |
| `FuseFold (RankKFull r, k, i)` | `reduce(NAME, k[, i], axes = r)` | §3.3 |
| `FuseFoldNested (…)` | `reduce(reduce(NAME, k1[, i1]), k2[, i2])`, inner result rank 1 | §3.4 |
| `FuseRowApply (srcs, p, k)` | `(method_for(…, NAME, …) <@> k) \|> compute` | §3.5 |
| `FuseBroadcast (skel, sibs)` | `<elementwise skeleton over NAME> \|> compute` | §3.5 |

### Rejections, and what each one protects

| reject | example | why |
|---|---|---|
| `NoUse` | nothing reads `traj` | not a fusion question |
| `MultipleUses n` | `reduce(traj,(+)) + traj(3)` | the trajectory outlives the fold |
| `ConsumerNotAdjacent` | a statement between the `let rec` and the fold | fusing would reorder across it |
| `ImpureSpine "lambda"` | `lambda(x) -> reduce(traj,(+)) + x` | might never run |
| `ImpureSpine "if"` | `if c then reduce(traj,(+)) else 0.0` | conditional |
| `ImpureSpine "apply"` | `method_for(traj) <@> k` with no `\|> compute` | deferred; forced elsewhere |
| `UnsupportedConsumer "bare-name"` | `traj` | escapes |
| `UnsupportedConsumer "application"` | `traj(7)` | that is P1's snapshot, not a fusion |
| `UnsupportedConsumer "reduce over transpose"` | `reduce(transpose(traj,[0,1]),(+))` | **mixes across the leading axis** |
| `UnsupportedConsumer "reduce over sort"` | `reduce(sort(traj,k),(+))` | not in-order |
| `AxesOutOfRange (n, r)` | `axes = 3` on rank 2 | `inferReduce` rejects it too |
| `RankKNeedsInit` | `reduce(traj, lambda(a,b)->a+b, axes=2)` | `rankKDesugar` rejects it too; the splice has no seed either |

**No `reduce` spelling is ever misaligned.** `reduce` folds the INNERMOST `n`
axes, so `axes = n < r` is per-slice by construction and `axes = r` is an
in-order scalar accumulation. The misalignment risk lives entirely in the
*operand*: `transpose`, `sort`, a time-axis fft. Those are rejected by the
operand whitelist, not by an alignment analysis.

---

## 3. The splice

P1's `Windowed(K, R)` desugar emits, in place of the `StmtLet`, a statement
sequence. P2 adds four insertion points to it:

```
  let mut __ring_NAME : Array<elemT like Idx<K+1>, trailing…> = zeros
  <snapshot lets>                       (empty when fusing — see 3.7)
[A] <fused output alloc / accumulator init>
  <seed write → ring slot 0>            (only when a seed arm exists)
[B] <fused per-slice work at n = 0>     (only when a seed arm exists)
  for n in loopStart..N:
      <lag lets>
      <slice compute; write → ring slot n mod (K+1)>
[C]   <fused per-slice work at n>
[D] let <fusedName> = <accumulator | output buffer>
  // NO binding for NAME
  <tail, with the consumer node replaced by `fusedName`>
```

`[D]`'s name is what `FusionPlan.Rebuild` substitutes for the consumer node.

### 3.0 The slice value the fused work consumes

`sliceCopyStmts` (TypeCheck.fs:12530) already materializes the slice into
`__slice_NAME` before the copy nest — but ONLY for rank >= 2. For rank 1
(`trailing = []`) it assigns `buf(n) = srcExpr` directly, with no binding.

**Decision:** `sliceCopyStmts` grows an "always bind" mode, used only when
fusing, so the rank-1 path also emits `let __slice_NAME = srcExpr` and assigns
from it. The non-fused emission stays byte-identical (the mode is off), and the
fused work never re-reads the ring or duplicates the slice expression. With a
seed arm, the peeled work at `[B]` consumes `__seed_NAME` the same way.

### 3.1 `FuseFold (Rank1Full, kernel, init)` — scalar accumulator, rank 1

Per-slice value `s_n` = `__slice_NAME` (a scalar).

`combined(acc, s)` is spelled exactly as `rankKDesugar` spells it
(TypeCheck.fs:5922-5925): `ExprBinOp(Elementwise, op, acc, s)` for an operator
section, `ExprApp(kernel, [acc; s])` otherwise.

* **with init** —
  `[A] let mut __accU = <init>` ; `[C] __accU = combined(__accU, s_n)` ;
  `[D] let fused = __accU`. Covers all N steps from the seed: R4's rule.
* **seedless, seed arm present** (`loopStart = 1`) — `[B]` is
  `let mut __accU = s_0`, `[C]` is `__accU = combined(__accU, s_n)`. The seed
  slice IS element 0, so this is R4's seed-with-first with the peel already
  done for us. No branch in the loop.
* **seedless, no seed arm** (`loopStart = 0`) — `[A] let mut __accU = <zed>`
  (never read), `[C] if n == 0 then __accU = s_n else __accU = combined(__accU, s_n)`.
  One predictable branch per time step, against a whole slice computation.
  Seeding with an identity instead would NOT be exact (`0.0 + (-0.0)` is
  `0.0`, not `-0.0`), and the interpreter differential is byte-exact.

**Empty fold:** `inferRecArray` rejects `N < 1` (:12477), and N is a static
literal, so the seedless fold can never be empty and no `BL8003` guard is
needed. That matches what the unfused form emits (`reduceNonEmpty` is true).

### 3.2 `FuseFold (PartialRowFold (n1, outRank), kernel, init)` — per-slice output

* `[A]` allocates the output over `Idx<N> :: <trailing minus the folded n1
  axes>`, through the same `zerosOver` helper (TypeCheck.fs:12511) and the same
  annotation-slot list, so the >4096-cell case takes the zero-FORMER branch
  exactly as the ring does.
* `[B]`/`[C]` emit `<out>(n) = reduce(__slice_NAME, kernel[, init][, axes = n1])`
  — the SAME `ExprReduce` node the row-mode apply would have run on that row.
  When `outRank > 1` the write goes through the `sliceCopyStmts` nest instead
  of a scalar assign.
* `[D] let fused = <out>`.

**Why synthesize the `reduce` rather than hand-roll the fold:** it inherits
every gate for free — `unitGate`, `compactGate`, `emptyGate` (:5694-5736),
`checkFoldOmpLicense`, the seed rule, the interpreter path — and value identity
holds by construction, because it is literally the same expression evaluated on
the same row.

### 3.3 `FuseFold (RankKFull r, kernel, init)` — scalar accumulator over all cells

`[A] let mut __accU = <init | 0 | 1>` (R2 always has a seed).

`[C]` must fold the slice's cells **directly into `__accU`**, i.e. the
`List.foldBack2` nest of TypeCheck.fs:5927-5930 restricted to the trailing
axes, body `__accU = combined(__accU, __slice_NAME(j0, .., jk))`.

**It must NOT be `__accU = combined(__accU, reduce(__slice_NAME, op, axes = r-1))`.**
That is a different association — `a + (b + c)` versus `(a + b) + c` — and
floating-point addition is not associative, so the value would drift from R2's.
This is the one place where the "synthesize the same node" trick does not
apply.

### 3.4 `FuseFoldNested` — the inner fold's scalar, accumulated

`[C]` computes `s_n = reduce(__slice_NAME, k1[, i1][, axes = n1])` (§3.2's
per-slice expression, unbound) and then accumulates it into `__accU` by §3.1's
rules with `k2`/`i2`. Associativity-safe: the outer fold's operands are exactly
the per-slice scalars, in order, which is what R4 over the N-vector computes.
Neither the trajectory nor the N-vector materializes.

### 3.5 `FuseRowApply` / `FuseBroadcast` — the output materializes

* `[A]` allocates the output over `Idx<N> :: <the kernel's result shape>`.
  For `FuseRowApply` the result shape is not syntactically known, so the
  allocation is built from the TYPED kernel result after the per-slice
  expression is inferred once — which means `[A]` is emitted after `[C]` is
  typed, not before. (Mechanically: type the per-slice expression against the
  slice type, read its shape, then build the buffer let and prepend it.)
* `[C]` for `FuseRowApply`: `ExprApp(kernel, args)` where arg `p` is
  `__slice_NAME` and arg `i != p` is `src_i(n)`. Write via `sliceCopyStmts`
  (array result) or a scalar assign.
* `[C]` for `FuseBroadcast`: substitute `__slice_NAME` for NAME in the
  skeleton (`Unfold.substFree`), then write the same way.
* `[D] let fused = <out>`.

**P2 restriction on `FuseBroadcast`:** every sibling must be a SCALAR
(obligation `IsScalar`). A leading-aligned array sibling would need an
`(n)`-indexed read inserted at its leaf, which is a second rewrite with its own
alignment argument; deferred. `traj * 2.0 |> compute` is the shape that matters
and it is scalar-sibling by construction.

**Residual risk, `FuseRowApply`:** a direct `ExprApp` of the kernel is not
*syntactically* the same node as the `<@>` row-mode apply, so a shape that
`LinAlgPatterns` would route to BLAS could in principle differ in the last bits
from a hand-applied kernel. Scalar-valued kernels (every corpus case) cannot.
Mitigation: value-identity tests against §4's oracle, and decline fusion if the
per-slice expression types to anything the plain apply would have routed
specially.

### 3.6 Obligations

`classify` returns syntactic proposals; these must discharge with `TypeEnv` in
hand or the plan is dropped and P1's ordinary path runs:

| obligation | check |
|---|---|
| `LeadingExtentIs (src, N)` | a co-iterated `method_for` source's leading extent equals N |
| `ScalarOrLeadingAligned (sib, N)` | P2: the sibling is a scalar |
| `DenseStaticScalarCells` | R2's own bounds: plain, `SymNone`, static extents, scalar element |
| `KernelDoesNotAskOmp` *(to add in Phase B)* | the fold kernel carries no `omp` — otherwise the serial splice would silently drop the clause (§1) |

### 3.7 Interaction with snapshots, and the binding structure

**Fusion and snapshots are mutually exclusive by construction.** A snapshot
read `NAME(p, …)` is itself an occurrence of `NAME`, so any non-empty `R`
makes the count >= 2 and `MultipleUses` fires. Therefore:

> `Fusable ⇒ R = ∅`

The fused sweep only ever carries the ring. That removes the whole class of
"snapshot copy versus fused write ordering" questions. Corpus draft `f05` is
the control that pins it: the same recursion with one extra static read
demotes to ring + snapshot.

**`NAME` dissolves**, exactly as in P1: no binding for it is emitted, so a
disagreement between the classifier and the rewriter is an unbound-variable
compile error rather than silent corruption. The consumer's result takes its
place under a fresh `__fuseU_NAME` name, and `Rebuild` substitutes it into the
tail.

*Optional refinement:* when the consumer is the ENTIRE value of the next
`let s = …` with a plain `PatVar`, bind the sweep's output directly to `s` and
drop the statement, instead of emitting `let s = __fuseU_NAME`. Saves a full
copy of the N-vector. The `let`'s annotation, if any, must move onto the fused
binding.

### 3.8 Diagnostic

The §8 verdict grows a fusion clause. Exact text to be agreed with P1 in
Phase B; the shape:

```
recursive array 'traj': windowed, K=1, slots=2, fused (fold(partial axes=1 rank=1))
recursive array 'traj': windowed, K=1, slots=2, snapshots=[63]   // fusion declined: multiple-uses(2)
```

`RecFusion.describeShape` / `describeReject` are the renderers.

### 3.9 `BLADE_FP_REASSOC`

With `BLADE_FP_REASSOC=1` and a reorder-licensed kernel, the UNFUSED rank-1
fold emits an 8-lane reassociated chain (CodeGen.fs:3416-3434, `fpReassocEnabled`
at :438). The fused sweep is a serial chain. So under that opt-in knob the two
forms can differ in the last bits.

This is documented, not fixed: the knob is an explicit opt-in to
reassociation, and section 9 of the parent plan defines the sweep's
enumeration order as the fold order. Value-identity tests must not run with
`BLADE_FP_REASSOC=1` set.

---

## 4. Oracle-verified corpus drafts

**These drafts have landed as `tests/corpus/recursive-arrays/030-038`** (see
that commit for the shipped versions, which use smaller N so the suite stays
fast and carry `// WARN: BL4017` pins in both directions). The manifest below
is the Phase-A record — written and run against the pre-fusion compiler, whose
values are the oracle because storage policy cannot move a printed digit.
Two of them changed disposition when the splice was built:

* **f03**'s consumer (`method_for(g) <@> lambda(x) -> x * x |> compute`) is a
  GENERAL row-apply kernel and therefore declines (§5). Its role is taken by
  corpus `033`, which uses the broadcast skeleton instead.
* **f07**'s rank-k full fold declines (§5); it has no corpus successor.

Run with `export PATH="/c/msys64/ucrt64/bin:$PATH"` or `blade run` fails
silently.

### f01 — loss trace (v2a, R1 partial fold, N = 500, K = 1, seed arm)

```blade
let step0: Array<Float64 like Idx<4>> = [1.0, 0.5, 0.25, 0.125]

function decay(r: Array<Float64 like Idx<4>>) -> Array<Float64 like Idx<4>> =
    method_for(r) <@> lambda(x) -> x * 0.997 + 0.001 |> compute

let (t0, t1, t250, t499, ttot) = {
    let rec traj: Array<Float64 like Idx<500>, Idx<4>> =
        match traj with
        | zero -> zero
        | zero :: s -> zero :: step0
        | prefix :: n -> prefix :: decay(prefix(n - 1))
    let trace = reduce(traj, (+))
    (trace(0), trace(1), trace(250), trace(499), reduce(trace, (+)))
}
```

verdict `fold(partial axes=1 rank=1)`; 500x4 trajectory rings at 2 slices, the
500-vector trace materializes.

```
step0 = [1, 0.5, 0.25, 0.125]
t0 = 1.875
t1 = 1.873375
t250 = 1.58891028654155
t499 = 1.45428618390571
ttot = 807.025558215334
```

### f02 — scalar total (v2a, nested collapse, N = 300, K = 2, seed arm)

```blade
let step0: Array<Float64 like Idx<3>> = [0.75, 0.5, 0.25]

function mix(a: Array<Float64 like Idx<3>>, b: Array<Float64 like Idx<3>>) -> Array<Float64 like Idx<3>> =
    a * 0.6 + b * 0.35 + 0.004 |> compute

let total = {
    let rec traj: Array<Float64 like Idx<300>, Idx<3>> =
        match traj with
        | zero -> zero
        | zero :: s -> zero :: step0
        | prefix :: n -> prefix :: mix(prefix(n - 1), prefix(n - 2))
    reduce(reduce(traj, (+)), (+))
}
```

verdict `fold(nested-scalar)`; K = 2 so the ring is 3 slots, and nothing but a
scalar materializes.

```
step0 = [0.75, 0.5, 0.25]
total = 95.5196854322221
```

### f03 — elementwise square then reduce (v2b row apply, rank 1, N = 200, NO seed arm)

```blade
let (q0, q1, q199, qtot) = {
    let rec g: Array<Float64 like Idx<200>> =
        match g with
        | zero -> zero
        | zero :: s -> zero :: 0.5
        | prefix :: n -> prefix :: prefix(n - 1) * 0.99 + 0.002
    let sq = method_for(g) <@> lambda(x) -> x * x |> compute
    (sq(0), sq(1), sq(199), reduce(sq, (+)))
}
```

verdict `compute(row-apply 0/1)`. This draft has a seed arm, so `loopStart = 1`
and slice 0 is peeled; f08 is its no-seed-arm twin.

```
q0 = 0.25
q1 = 0.247009
q199 = 0.0578883125883888
qtot = 22.8336735602595
```

### f08 — no seed arm, `loopStart = 0` (both v2a and v2b, N = 200)

The mirror of f01/f03: with no `| zero :: s` arm the sweep starts at n = 0 and
slice 0 is written INSIDE the loop, so a seedless fold has no peeled first
element to seed from — this is the draft that exercises §3.1's in-loop `n == 0`
branch. Implicit zero history supplies `prefix(-1) = 0`, so `g(0) = 0.002`,
which also keeps the ring's zero-history guard under test.

```blade
let (g0, g1, g199, gtot) = {
    let rec g: Array<Float64 like Idx<200>> =
        match g with
        | zero -> zero
        | prefix :: n -> prefix :: prefix(n - 1) * 0.99 + 0.002
    let sq = method_for(g) <@> lambda(x) -> x * x |> compute
    (sq(0), sq(1), sq(199), reduce(sq, (+)))
}

let gsum = {
    let rec g2: Array<Float64 like Idx<200>> =
        match g2 with
        | zero -> zero
        | prefix :: n -> prefix :: prefix(n - 1) * 0.99 + 0.002
    reduce(g2, (+))
}
```

verdicts `compute(row-apply 0/1)` and `fold(rank1-full)`.

```
g0 = 4e-06
g1 = 1.58404e-05
g199 = 0.0299996481423648
gtot = 3.07580578414165
gsum = 22.8527975621876
```

### f04 — STREAM diagnostic, physics/46 `superT` shape (v2b, N = 400, K = 1)

```blade
let initE: Array<Float64 like Idx<8>> = [1.0, 0.0, 0.5, 0.0, 0.25, 0.0, 0.125, 0.0]

function vstep(r: Array<Float64 like Idx<8>>) -> Array<Float64 like Idx<8>> =
    method_for(range<Idx<8>>) <@> lambda(i) -> r(i) * 0.998 + r(7 - i) * 0.0015 |> compute

let (d0, d1, d200, d399, dtot) = {
    let rec superT: Array<Float64 like Idx<400>, Idx<8>> =
        match superT with
        | zero -> zero
        | zero :: s -> zero :: initE
        | prefix :: n -> prefix :: vstep(prefix(n - 1))
    let obs = method_for(superT) <@> lambda(row: Array<Float64 like Idx<8>>) -> (row(0) + row(2) + row(4) + row(6)) / 4.0 |> compute
    (obs(0), obs(1), obs(200), obs(399), reduce(obs, (+)))
}
```

verdict `compute(row-apply 0/1)`.

```
initE = [1, 0, 0.5, 0, 0.25, 0, 0.125, 0]
d0 = 0.46875
d1 = 0.4678125
d200 = 0.328310326799586
d399 = 0.249833315847781
dtot = 135.480752791699
```

### f07 — rank-k full fold, both seed spellings (v2a, R2, N = 120)

```blade
let step0: Array<Float64 like Idx<3>> = [0.75, 0.5, 0.25]

function decay3(r: Array<Float64 like Idx<3>>) -> Array<Float64 like Idx<3>> =
    method_for(r) <@> lambda(x) -> x * 0.995 + 0.002 |> compute

let gridSum = {
    let rec traj: Array<Float64 like Idx<120>, Idx<3>> =
        match traj with
        | zero -> zero
        | zero :: s -> zero :: step0
        | prefix :: n -> prefix :: decay3(prefix(n - 1))
    reduce(traj, (+), axes = 2)
}

let gridSumSeeded = {
    let rec traj2: Array<Float64 like Idx<120>, Idx<3>> =
        match traj2 with
        | zero -> zero
        | zero :: s -> zero :: step0
        | prefix :: n -> prefix :: decay3(prefix(n - 1))
    reduce(traj2, (+), 0.0, axes = 2)
}
```

verdict `fold(rank2-full)` for both. Pinned together on purpose: the seed IS
the identity here, so the two spellings must agree — which is what makes §3.3's
association argument checkable.

```
step0 = [0.75, 0.5, 0.25]
gridSum = 171.120822870597
gridSumSeeded = 171.120822870597
```

### f05 — CONTROL: a later static read demotes

```blade
let step0: Array<Float64 like Idx<4>> = [1.0, 0.5, 0.25, 0.125]

function decay(r: Array<Float64 like Idx<4>>) -> Array<Float64 like Idx<4>> =
    method_for(r) <@> lambda(x) -> x * 0.997 + 0.001 |> compute

let (last, comp0, comp3) = {
    let rec traj: Array<Float64 like Idx<64>, Idx<4>> =
        match traj with
        | zero -> zero
        | zero :: s -> zero :: step0
        | prefix :: n -> prefix :: decay(prefix(n - 1))
    let trace = reduce(traj, (+))
    (trace(63), traj(63, 0), traj(63, 3))
}
```

verdict `NOT:multiple-uses(2)`. P1 windows this as `Windowed(K=1, R=[63])`.

```
step0 = [1, 0.5, 0.25, 0.125]
last = 1.78159033744297
comp0 = 0.885034261468274
comp3 = 0.160926793291164
```

### f06 — CONTROL: a consumer that mixes across the leading axis

```blade
let step0: Array<Float64 like Idx<4>> = [1.0, 0.5, 0.25, 0.125]

function decay(r: Array<Float64 like Idx<4>>) -> Array<Float64 like Idx<4>> =
    method_for(r) <@> lambda(x) -> x * 0.997 + 0.001 |> compute

let (c0, c1, c2, c3) = {
    let rec traj: Array<Float64 like Idx<64>, Idx<4>> =
        match traj with
        | zero -> zero
        | zero :: s -> zero :: step0
        | prefix :: n -> prefix :: decay(prefix(n - 1))
    let percomp = reduce(transpose(traj, [0, 1]), (+))
    (percomp(0), percomp(1), percomp(2), percomp(3))
}
```

verdict `NOT:unsupported-consumer(reduce over transpose)`. The transpose puts
the recursion axis innermost, so the fold runs ALONG TIME. The four values are
per-component sums over the whole trajectory — deliberately different numbers
from any per-step fold, so a mistaken fusion is caught by the values and not
only by the verdict pin.

```
step0 = [1, 0.5, 0.25, 0.125]
c0 = 60.2069471053766
c1 = 31.0517367763441
c2 = 16.4741316118279
c3 = 9.18532902956982
```

---

## 5. What shipped, and what declines

The splice implements the families whose OUTPUT TYPE is derivable from the
recursion's own annotation. That is the single criterion, and it is not
fussiness: the alternative is typing the per-slice expression speculatively,
and a speculative typing that then declines has already minted index-record
ids and shifted every id downstream — exactly what P1's syntactic-first
ordering exists to prevent, and what the emit A/B would show as a spurious
diff on an unrelated file.

| shape | disposition | corpus |
|---|---|---|
| `reduce(NAME, k[, i])`, rank 1 | **fused** (`RecFuseAccum`) | 032 |
| `reduce(NAME, k[, i])`, rank >= 2 | **fused** (`RecFuseBuffer`) | 030 |
| `reduce(reduce(NAME, k1), k2)` | **fused** (`RecFuseAccum`) | 031 |
| `method_for(NAME) <@> lambda(row) -> reduce(row, …) \|> compute` | **fused**, canonicalized onto the partial-fold route | 034 |
| `<elementwise skeleton over NAME> \|> compute` | **fused** (`RecFuseBuffer`) | 033 |
| `method_for(NAME) <@> <general kernel> \|> compute` | declines: result shape not derivable | 035 |
| `reduce(NAME, k, axes = rank)` | declines: see below | — |
| any kernel carrying `where … omp` | declines: the splice would serialize it silently | 038 |

**The rank-k full fold** (`axes = rank`) is recognized by the classifier and
declined by `recFusePlan`. Two things it needs that nothing else does: the
`(+)`/`(*)` identity constructed for its seed (which `rankKDesugar` does at
TypeCheck.fs:5893-5911, and which has no complex case), and — the real reason
— its per-slice work must fold the slice's cells **directly** into the
accumulator. `op(acc, reduce(slice, op))` is a *different association*, and
floating-point addition is not associative, so the value would drift from
`rankKDesugar`'s. Every other tier gets value identity for free by
synthesizing the very node the unfused route would have evaluated; this one
cannot, so it is the one that has to be written out longhand. It is also
absent from the corpus. Deferred, deliberately.

**The general row apply** is the honest gap. physics/46's `superT` consumer
is a real STREAM site and it does not fuse. Teaching it to would mean either
(a) typing the per-slice expression in a scratch environment that can be
discarded without shifting ids, or (b) an annotation on the consumer binding.
(a) is the principled fix and it is a P3-sized change to the elaborator, not
a P2 one.

## 6. Verification record

| instrument | result |
|---|---|
| `blade test recarrays` | **31/31** (22 P1 + 9 new) |
| `blade test rec-fusion` | **32/32** |
| `blade test interp recursive-arrays` | **31 passed, 0 failed**, 1 pre-existing SKIP (provider write) |
| `blade test interp memfree` | **19 passed, 0 failed**, 2 pre-existing SKIP |
| value A/B vs frozen baseline, 34-file default list | **34 IDENTICAL, 0 DIFFERS** |
| emit A/B vs frozen baseline, same list | 26 IDENTICAL, **8 DIFFERS** — see below |

The 8 emit diffs are P1's 7 windowed files (`memfree/009`, `memfree/021`,
`spectra/048`, `spectra/053`, `spectra/054`, `sgs/006`, `sgs/015` — all
BL4017-pinned) plus `recursive-arrays/007_rk4_litmus`, whose **entire** diff is
a `final` → `final_` C++ keyword escape from master commit `04bd3da` ("Fix two
CSV-provider write bugs: keyword names …"), which post-dates the frozen
baseline `d3e005d` and arrived through the P1 merge. Verified two ways: that
commit is not an ancestor of the baseline, and undoing the rename makes the two
`.cpp` files byte-identical. P2's own post-merge commits touch only
`src/RecFusion.fs`, `src/TypeCheck.fs`, `tests/Test_RecFusion.fs` and the new
corpus files — no codegen file at all.

**No P1-windowed file flipped to fusion.** All seven still report
`snapshots=[…]` with no `fused=` clause; P2 only takes tails that P1's
snapshot scan rejected with `RecBareEscape`.

## 7. Remaining work

1. **The general row apply** (§5) — the one real gap. Needs a discardable
   typing environment so the per-slice result shape can be read without
   committing ids.
2. **The rank-k full fold** (§5) — needs the direct cell nest, not a nested
   `reduce`, for the association reason. No corpus demand today.
3. **Broadcast siblings beyond scalars.** A leading-aligned ARRAY sibling
   (`traj * weights |> compute`) needs an `(n)`-indexed read inserted at its
   leaf — a second rewrite with its own alignment argument. Today
   `recFusePlan` requires every sibling to be a scalar of the element type,
   checked without inference (a numeric literal of the right class, or a bound
   name whose recorded type already resolves to it).
4. **`BLADE_FP_REASSOC`** (§3.9) stays a documented divergence.
5. **Fusion + snapshots together.** Today mutually exclusive by construction.
   Supporting `reduce(traj, (+))` *and* `traj(63)` in one tail would mean the
   fusion classifier tolerating snapshot reads as non-uses — sound, but it
   gives up the invariant that keeps the splice ordering-free. Corpus `036` is
   the case that would light up.
