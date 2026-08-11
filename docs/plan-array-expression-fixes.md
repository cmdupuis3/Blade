# Array-expression seams: bug inventory and fix plan

Found 2026-08-08 while writing a generalized Lomb–Scargle DFT (frequency × time
outer space, per-frequency reductions, complex output, units on both axes). That
one program touches most of the array-expression surface, so the failures below
are a reasonable cross-section rather than a list of curiosities.

Everything here was reproduced by **compiling and running** minimal cases against
`master` + the uncommitted `src/` changes, not by reading source. Each repro is
inlined so this document stays useful after the scratch files are gone.

---

## 1. Compiler issues

Status column added 2026-08-08 after the multi-agent fixing round; per-theme
sections below carry the corrected root causes where the original attribution
was wrong (#4, #5).

**Re-verified 2026-08-10** against a fresh build of `master` @ `a0eb07a`, by
re-running every repro. Three rows changed: #3 and #9 were marked Open but are
actually fixed; #6 was marked FIXED but is only partial. Everything else held.
The headline result is that the "ideal" formulation this document was written to
justify — abstract unit-carrying params, a nested loop object inside a kernel,
a complex `exp` map, `prodsum` over a kernel-local array — now compiles, runs,
and reproduces the numpy reference to all printed digits.

| # | Sev | Category | Issue | Symptom | Status (2026-08-08) |
|---|-----|----------|-------|---------|---------------------|
| 1 | **Critical** | Semantics | `object_for(k) <@> (A, zip(B, C))` silently degrades to a 3-way outer product instead of co-iterating the zip | Compiles, runs, returns \|A\|·\|B\|·\|C\| cells with no diagnostic | **GUARDED** — BL3999 diagnostic; real co-iteration feature still open (Theme A) |
| 2 | High | Codegen | `method_for(A, zip(B, C))` miscompiles | Undeclared `arr1` / `__v10` in emitted C++; `for (A, zip(B,C))` sugar fails identically | **GUARDED** — same BL3999, both orientations + `for` sugar |
| 3 | High | Codegen | Nested loop object inside a kernel body | `LOOP_OBJECT_USED_AS_VALUE` + `REDUCE_OVER_A_DEFERRED_COMPUTATION_MUST_BE_BOUND_TO_A_LET` | **FIXED** — re-verified 2026-08-10 @ a0eb07a: the repro below compiles, runs, and is numerically correct. (Status was stale.) |
| 4 | High | Units | Kernel param's unit is dropped when the other operand is a **dimensioned captured scalar** | False `BL3006` on a correctly dimensionless product | **FIXED** — see Theme C (root cause was first-pass, not the walk) |
| 5 | Med | Codegen | `\|> compute` on an elementwise array expression **inside a function body** | `ARRAY-VALUED_ELEMENTWISE_KERNEL_BODY_NOT_SUPPORTED_INSIDE_A_KERNEL`; the same expression *without* `compute` works, and the same `compute` at top level works | **FIXED** — see Theme B (double `IRCompute` wrap, not the guard) |
| 6 | Med | Codegen | `A [*] B \|> compute` emits a loop object, never finishing its `<@> (op)` | `LOOP_OBJECT_USED_AS_VALUE(std::make_tuple(a, b))` | **FIXED** — bare form fixed earlier; the unary-negated residual (`-(tau [-] ts)`) fixed 2026-08-10 on `fix/negated-bracketed-op`. Root cause was NOT the lift pass: **unary minus over any array was unimplemented** (`-A` on a plain array miscompiled too), because only the `OpConj` half of the whole-array routing at `TypeCheck.fs:7368` was ever written. Three parts: (a) route `OpNeg` on an `ArrayElem` to `TExprArrayNegate`; (b) `IRArrayNegate`/`IRArrayConjugate` lift their child with `liftChildIncludingLoopApp` so a bracketed-op operand materializes; (c) `IRCompute` of an already-eager whole-array form re-dispatches to the eager arm. (c) also fixes a pre-existing sibling bug: `conj(z) \|> compute` failed on master while `conj(z)` worked. Pinned by `tests/corpus/bracketed/015` and `index-types/238`. |
| 7 | Med | Types | `abs <@> complexArray` keeps the **complex** element type (imag = 0) | Complex type propagates into every downstream binding | **FIXED** — `adoptBodyElem`, both adoption sites |
| 8 | Med | Parse | `T<unit>^r` doesn't parse — a type var takes a rank caret **or** type args, never both | `BL1001: Expected ')' but got '^'`; blocks units on abstract params entirely | **FIXED** — trailing caret + unit-carrying `TyAbstractArray` lowering |
| 9 | Med | Expressiveness | `prodsum` row mode requires a **concrete** row annotation | `prodsum() requires array arguments` for `lambda(r: T^1)`; no prodsum-using function can be generic | **FIXED** — re-verified 2026-08-10 @ a0eb07a: `(cw <@> lambda(r: T^1) -> prodsum(r, s)) \|> compute` checks and runs. (Status was stale.) |
| 10 | Low | Parse | `let a, b = <tuple>` doesn't parse | `BL1001: Expected '=' but got ','` | **PARTIALLY LANDED 2026-08-08** — the *construction* half landed: a bare comma on a `let` RHS now builds a `Tuple<N>` at any width (`let t = b, c`), the same node a parenthesized literal produces (`tests/corpus/tuples/001`). The *destructuring* half (`let a, b = t`, no parens) is still deferred; `let (a, b) = t` keeps working (`tuples/007`). See `docs/plan-tuples-vs-arg-packs.md` §6b/§9. |
| 11 | Low | Parse | Tuple patterns in lambda params — `lambda(f, (t, v))` | `BL1001: Expected ')' but got ','` | **PARTIALLY LANDED 2026-08-08** — tuple-*annotated* params landed: `lambda(p: Tuple<2>)` and a fully written tuple type `(T1, T2)` both work as kernel/function parameters, with `p[k]` projection standing in for destructuring (`tests/corpus/tuples/002`, `011`). Tuple *patterns* (`lambda((a, b))`) still don't parse — confirmed still `BL1001`, still deferred. See `docs/plan-tuples-vs-arg-packs.md` §6b/§9. |
| 12 | Low | API | `complex(a, b)` is scalar-only, no array lifting | `BL3001: expected Float64, got Array<…>` | **FIXED** — synthesize-and-infer lift, incl. scalar broadcast both orders |
| 13 | Low | API | `reynolds(namedFunction, Antisymmetric)` rejected | `reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda` | Open |
| 14 | Low | API | No `atan2` — every `mathIntrinsics` entry is unary | `arg(complex(x, y))` is an exact substitute, so possibly intentional | **FIXED** — `atan2`/`log_base` as `Ast.OpMath2` (a call-rendered binop, like `^`), with the `complex(a, b)` array lift; `log10` added unary |

### Minimal repros

**#1 — silent 3-way outer.** Expected 6 cells (2×3); got 18 (2×3×3).

```blade
let w: Array<Float like Idx<2>> = [1.0, 2.0]
let t: Array<Float like Idx<3>> = [1.0, 2.0, 3.0]
let s: Array<Float like Idx<3>> = [10.0, 100.0, 1000.0]
let K = object_for(lambda(f, ti, v) -> v * f * ti)
let grid = K <@> (w, zip(t, s)) |> compute
// grid = [10, 100, 1000, 20, 200, 2000, 30, 300, 3000,
//         20, 200, 2000, 40, 400, 4000, 60, 600, 6000]
```

**#2 — same shape, `method_for` orientation.**

```blade
let grid = method_for(w, zip(t, s)) <@> lambda(f, ti, v) -> v * f * ti |> compute
// error: 'arr1' was not declared in this scope
```

**#3 — nested loop object in a kernel.**

```blade
let out = w <@> lambda(f) -> {
    let wt = f * t
    let c = sin <@> wt
    reduce(c, (+))
} |> compute
```

**#4 — unit walk.** The discriminating matrix, all with
`ws : Array<Float<invday> like I>`, `tz : Float<day>`, `c : Float64`:

| body | expected | actual |
|------|----------|--------|
| `cos(w)` | reject `1/day` | ✓ rejects `1 / (day)` |
| `cos(w * w)` | reject `1/day²` | ✓ rejects `1 / (day^2)` |
| `cos(w * d)`, `d` a param of a `Float<day>` array | accept | ✓ accepts |
| `cos(w * c)`, dimensionless capture | reject `1/day` | ✓ rejects `1 / (day)` |
| `cos(w * tz)`, **dimensioned capture** | accept | ✗ rejects, reports `day` |
| `cos(tz * w)`, order swapped | accept | ✗ rejects, reports `day` |

So the param's unit is read correctly in isolation, cancels correctly against
another *param*, and survives a *dimensionless* capture — it is dropped only
against a **dimensioned captured scalar**, in both operand orders. The result is
always exactly the capture's own signature.

**#5 — `compute` inside a function body.**

```blade
function varC(x: T^1) = {
    let n = 1.0 * extents(x)
    let xbar = reduce(x, (+)) / n
    let dx = (x - xbar) |> compute   // rejected
    // let dx = x - xbar             // identical line, compiles and runs
    prodsum(dx, dx) / n
}
```

**#7 — `abs` over a complex array.** `R`, and everything downstream of it,
comes out `std::complex<double>` with a zero imaginary part.

```blade
let z2 = (zip(C2, S2) <@> lambda(a, b) -> complex(a, b)) |> compute
let R = (abs <@> z2) |> compute
// R = [(0.654146811494467,0), (1.64276193063514,0), ...]
```

---

## 2. Documentation drift

Compiler is right, docs are wrong.

| # | Where | Claim | Reality |
|---|-------|-------|---------|
| D1 | `docs/features/sql.md` §10 | `reduce : Array<T like I..., J> -> result over I...` (innermost axis only) | **REVERSED, then FIXED IN THE COMPILER 2026-08-09.** The 2026-08-08 entry read "docs wrong, compiler right (full fold to a scalar)" and rewrote §10 to match the compiler. The language owner then ruled the ORIGINAL doc claim was the intended design and the full fold was the drift: `reduce` now folds RIGHT-TO-LEFT, **one axis by default** (rank-2 `[[1,2,3],[10,20,30]]` → `[6, 60]`), with the full fold spelled `axes = n` (n = rank). §10 rewritten again to the new semantics; the row-wise `A <@> lambda(r) -> reduce(r, (+))` idiom stays documented as the equivalent long form. Corpus full-fold pins migrated to explicit `axes`. |
| D2 | `quickstart-1.md` §3 | `let e, d, f = myTuple` | Doesn't parse (issue #10's destructuring half, still true). **FIXED 2026-08-08** — `quickstart-1.md` §3 rewritten to the Design C semantics (`docs/plan-tuples-vs-arg-packs.md` §6b): the parenthesized destructuring form `let (e, d, f) = myTuple`, plus the now-landed bare-comma *construction* spelling and `t[k]` projection. |
| D3 | `quickstart-1.md` §5, `quickstart-2.md` "Array Combinators" | A zip kernel "receives ONE tuple argument"; `let add((a, b)) = a + b` | Compiler wants flattened params (`lambda(u, w)`), and the tuple spelling doesn't parse (issue #11). **FIXED 2026-08-08, revised same day for §6c** — both rewritten to the width-schema rule (`docs/plan-tuples-vs-arg-packs.md` §6c, which supersedes §6b's deep-flatten with one-level structural matching; §9): the loop former decides *iteration* (`zip` vs `,`), the kernel's *written* parameter shape decides *packing*. The old unparseable pattern spelling `add((a, b))` is replaced with the now-working `Tuple<N>` annotation (`function add(p: Tuple<2>) = p[0] + p[1]`, verified against a real build — note the inferred, not explicit `-> T^0`, return type: an explicit abstract return type on a `Tuple<N>`-parameterized named function currently hits `BL6001` and is its own open item); tuple *patterns* remain issue #11's still-deferred half. |
| D4 | `quickstart-2.md` "Virtual Arrays" | `blocked<I, K>` is a built-in virtual array | No implementation found in `src/`: `ExprBlocked`/`IRBlocked` exist as internal AST/IR nodes (`Ast.fs:415`, `IR.fs:154`) reached only from `TypeCheck.fs`/`Lowering.fs`/`Zonk.fs`/`Unfold.fs`, but **no parser production ever constructs `ExprBlocked`** (`blocked` is not a lexer keyword, unlike `range`/`reverse`) and the interpreter explicitly raises `InterpUnsupported "IRBlocked standalone materialization (M2.7)"` (`Interp/Loops.fs:1617`). Confirms `docs/providers/ZarrVirtualArraysSpec.md`'s own framing of `blocked<I, K>` as "spec level" only. **FIXED** (false claim removed from quickstart-2.md 2026-08-08). |
| D5 | `quickstart-2.md`, `formalism.md` §14.3, `examples.md:176` | `<&!>` forced fusion | ~~Zero corpus coverage; its tuple result is unreachable anyway via #10~~ **CORRECTED 2026-08-08 — this premise was wrong.** `<&!>` lexes (`Lexer.fs:247`), parses (`Parser.fs:197,284,2511-2520`), type-checks, and has dedicated mandatory-fusion codegen (`CodeGen.fs:10402-11397`, several hundred lines). It has **21 corpus tests** using the literal token (`tests/corpus/loops/019_fusion_basic.blade` and 20 others, committed 2026-07-22, well before this plan), including the exact `(L <@> f) <&!> (L <@> g)` shape from the docs, destructured with a *parenthesized* tuple pattern `let (sums, prods) = ...` — which parses fine; only the unparenthesized `let a, b = ...` (issue #10) is broken. No doc changes made; the docs already match the implementation. |
| D6 | `quickstart-1.md` §4 | "The opposite end is `let const` (immutable everywhere)" | Found 2026-08-10: `let const x = 1.0` does not parse — `BL1001: Expected '=' but got identifier 'x'`. Either implement the modifier or drop it from the guide. |

## 3. Unspecified semantics

Not bugs — decisions that have never been written down, and that real code
depends on.

- **Does `prodsum` conjugate on complex operands?** `prodsum(e, e)` must mean
  `Σ eₜ²`, not `Σ|eₜ|²`. The Lomb–Scargle kernel relies on the unconjugated
  reading to get `Σ e^{2iωt}` from one complex multiply; a Hermitian reading
  would silently return `n`. Needs a corpus pin either way. **RULED 2026-08-08
  (coordinator): unconjugated.** Pin drafted at
  `tests/corpus/index-types/235_prodsum_complex_unconjugated.blade` — must be
  validated against actual behavior before landing (see corpus test's own
  header note). Documented in `docs/features/sql.md` §10a (new section, added
  next to `reduce` since `prodsum` had no prior doc home at all).
- **Is #11 (flattened zip params) design or oversight?** The whole corpus uses
  the flattened form; only the docs use the tuple form. Pick one. **Deferred —
  handled separately**: bundled with issues #10/#11 and doc items D2/D3 under
  the coordinator's untangling instruction (2026-08-08); out of scope for this
  fixing round.

---

## 4. Fix plan

Grouped by root cause rather than by issue number — several table rows are the
same underlying gap seen from two directions.

### Theme A — `zip` as an operand of a multi-array loop (#1, #2)

One missing feature, two failure modes. A `zip` in an operand pack should
contribute **one axis** and deliver **k values** to the kernel. Today
`object_for` flattens it into the array pack (→ a 3-way outer product, silently)
and `method_for` carries it to codegen unmaterialized (→ undeclared `arr1`).

- **P0 guard — LANDED 2026-08-08.** Both orientations (and the `for (A,
  zip(B,C))` sugar) now reject with a shared BL3999 diagnostic
  (`zipInMultiArrayPackMsg`) steering to hoisting the zip or passing the
  arrays as separate operands. An all-zip pack stays legal. Mechanism found
  on the way: `inferApply`'s `TExprObjectFor` arm spliced the zip's children
  into the pack, `allCoIter` then failed, `sharedRecords` fell to `[]`, and
  every array became an independent outer axis — with the kernel arity
  coincidentally matching, so nothing complained. The `method_for`
  orientation instead fell through `inferMethodFor`'s final arm with the zip
  as one never-materialized pack slot. Pins:
  `diagnostics/050_zip_in_object_for_pack`, `diagnostics/051_zip_in_method_for_pack`.
- **Real fix — still open, deliberately deferred this round.** Handle the zip
  at loop construction so both orientations share one path. The natural
  regression pin is the compose-apply duality already stated in
  `quickstart-2.md` and `formalism.md`:
  `object_for(f) <@> (A, zip(B,C)) ≡ method_for(A, zip(B,C)) <@> f`. Pin the
  cell *count* as well as the values — the count is what #1 got wrong. The
  guard sites above are exactly where the construction has to change: treat
  a zip operand as ONE iteration record delivering k kernel params, i.e.
  make `sharedRecords` see through the zip rather than flattening it.

### Theme B — kernel bodies are not full expression contexts (#3, #5)

- **#5 — FIXED 2026-08-08, and the attribution above was wrong.** The
  `arrayValuedComputeBody` guard never fires on this repro; what fired was the
  CodeGen backstop, reached because elementwise array ops *re-synthesize
  themselves* as `compute(method_for(zip ..) <@> k)` (`inferBinOp`'s zip and
  broadcast arms), so a user-written `|> compute` arrived already computed and
  was DOUBLE-wrapped: `IRCompute(IRCompute(IRApplyCombinator))`. Module-level
  `genComputeBinding` peels `IRCompute` recursively (double wrap invisible);
  `genFuncBodyScoped`'s let dispatch matches exactly one, so in a function
  body the value fell to the inline-expression rejection. Fix: `compute` is
  idempotent at inference — an operand already elaborated to `TExprCompute`
  is returned unwrapped. Neither guard was relaxed; both still fire
  (`loops/084` still rejects). Complementary to Theme D's fold, which
  prevents the wrapper over already-eager `IRApp(IRObjectFor …)` forms.
  Pin: `loops/114_compute_elementwise_in_function_body` (computed = uncomputed = 1.25).
- **#3 is the deep one** and the highest-value item in the document: it is what
  stands between the current implementation and the natural per-frequency
  formulation, where each kernel invocation does its own rank-1 work over
  captured arrays and no M×N grid is ever materialized. Likely wants a real
  design pass rather than a patch. Note the error messages already steer users
  correctly ("reduce the row to a scalar with prodsum or reduce"), so the
  workaround path is discoverable — this is a ceiling, not a trap.

### Theme C — the kernel-body unit walk (#4) — **FIXED 2026-08-08**

The hypothesis above was wrong in an instructive way: `kernelBodyUnits` was
never reached. The rejection fires during **first-pass body typing**, in the
scalar `OpMath` arm's dimensioned-operand catch-all (the
`unitRulesForUnaryOp` call under `inferExpr`'s math-intrinsic case): when
`w * tz` is typed, `w` is still an unresolved inference variable contributing
"no units", so `inferArithType` stamps the product with exactly the capture's
signature (`day`) — a provisional annotation — and the transcendental check
hard-errors on it before param unification and the authoritative second pass
ever run. That is why the reported unit is always exactly the capture's own
signature, in both operand orders, and why param/param products (both infer
vars, no annotation) were fine.

**Fix:** `TypeEnv.InLambdaBody` (set only in `inferLambda` body inference —
named-function decl bodies keep decl-time strictness, their unannotated params
are dimensionless by contract) + a deferral arm in the `OpMath` catch-all:
inside a lambda body, if the argument subtree still contains unresolved
inference vars (`typedExprHasUnresolvedType`), the unit rejection defers to
`kernelBodyUnits`, which reruns the same per-op table after unification.
Pins: `units/052_capture_unit_cancellation` (accept, both orders),
`unit-errors/002` (capture residue still rejects), `unit-errors/003`
(param residue with dimensionless capture still rejects).

**Round 2, same day — the ARRAY sibling.** Final verification found the same
premature-judgment pattern one level up: `exp <@> (i * w * ts)` inside a
kernel (lsdft line 36) false-rejected, because the *nested* apply's
`kernelBodyUnits` "second pass" runs during the *outer* lambda's body
inference, judging a provisional `day` stamped by the broadcast arm while `w`
was still unresolved. The real-valued twin `exp <@> (w * ts)` "passed" only
vacuously — the broadcast arm never fires when the scalar side is an
unresolved infer var, so the element carried *no* annotation and
`exp <@> (w * ts * ts)` (genuine residue) was a silent FALSE ACCEPT. Fix:
(1) nested-apply deferral in `buildApplyInfo` (unit errors → no-claim when
`env.InLambdaBody` and an operand subtree has unresolved types), plus
(2) recheck arms in `kernelBodyUnits` — `TExprApply`/`TExprCompute` now
recompute the nested map's element signature recursively
(`nestedOperandElemUnits`/`nestedApplyElemUnits`, binding nested kernel
params to walk-computed operand elem units) instead of falling to the
no-claim catch-all. Fixes the false reject AND the false accept. Pins:
`units/065_nested_map_capture_unit_cancels` (a `REJECT-AT: codegen` probe —
the accepting shape is the #3 ceiling by construction, so the asserted stage
IS the pin; convert to a value test when #3 lands), `unit-errors/004` (real
residue — new coverage, was the false accept), `unit-errors/005` (complex
residue).

Known residual seams, deliberately left: (a) the `^`/caret unit rule can
still premature-reject a provisional dimensioned base inside a kernel body
(same pattern, binop site — rarer, and threading the deferral through
`inferArithType` touches every op); (b) a lambda that is only ever called
directly (never `<@>`-applied) skips the transcendental unit check when a
param stays unresolved, since only the kernel-apply seam runs the second
pass/recheck.

### Theme D — combinator results used as values (#6) — **FIXED 2026-08-08**

The desugar was already complete: `lowerTypedBinOp`'s both-arrays branch
lowers `A [op] B` to a finished `IRApp(IRObjectFor kernel, [IRTuple [A; B]])`,
and bare `let g = a [*] b` worked. What broke was the `|> compute` wrapper:
`TExprCompute` wrapped the already-eager application in `IRCompute`, routing
it to `genComputeBinding` (which dispatches only the *deferred* combinator
shapes) and from there through the loop-object error sentinel. Fix: a
`computeWrap` fold in `Lowering.fs`'s `TExprCompute` arm drops the no-op
wrapper when the inner is a bare `IRApp(IRObjectFor …)`, letting the existing
expansion paths (genBinding's object-for arm, genFuncBody's hoistLoopApps) see
it. Pin: `bracketed/014_bracketed_compute` (`[*]`, `[+]`, `[-]`, `[^]`,
reduce-over-computed-outer, and the `method_for(A,B) <@> (*)` duality), plus
interp agreement.

### Theme E — type-level gaps (#7, #9, #12)

- **#7 — FIXED 2026-08-08.** Close to the guess above, one level deeper: the
  restamp walk already corrected the *body node*; what won was the resolved
  return-type var, which the scalar `abs` arm had typed at the deferred
  operand's own var, so param unification with Complex128 dragged the return
  complex. Fix: `adoptBodyElem` in `buildApplyInfo` — the existing
  real→complex adoption rule factored out and given its inverse, gated on the
  body's top node being `abs`/`real`/`imag`/`arg` over a complex operand,
  applied at both adoption sites. Pin: `intrinsics/008_abs_over_complex_array`.
- **#9:** row mode needs an abstract spelling. Until then, no library function
  using `prodsum` can be written generically — which is a real constraint on
  the stdlib, not just on user code. (Still open; `T<unit>^r` landing may make
  `lambda(r: T^1)` worth re-testing when this is picked up.)
- **#12 — FIXED 2026-08-08.** `complex(re, im)` now tries the scalar
  construction first and, on rejection with array operands, re-synthesizes as
  the same `method_for(zip(re,im)) <@> lambda(..) -> complex(..) |> compute`
  users hand-wrote, with array/scalar broadcast in both operand orders.
  Pin: `intrinsics/009_complex_array_lift`.

### Theme F — parser (#8, #10, #11)

**#8 is the one that matters — FIXED 2026-08-08** along the lines sketched:
`parseTypeAtom` accepts a trailing `^rank` after `buildTypeApp` and wraps in
`TyAbstractArray`; lowering decides whether the head is a type variable or a
concrete base. Chosen representation: `T<u>^k` lowers to a rank-k array whose
element is `IRTUnitAnnotated(<scalar type var>, u)` — byte-identical in shape
to a concrete `Array<Float<u> like I>` element, so `IR.getUnits`, the kernel
unit walks, and `unify`'s unit-compatibility check all read it with no new
mechanism. `T<u>^0` is the bare annotated element. Abstract axes get
`IRParam("?")` extents (never compared, print as `Idx<?>`). BL3015
(undeclared unit name) covered via a new `unitAnnoError` arm — the
TyAbstractArray shape would otherwise have silently opted out of that walk.
Pins: `units/060`–`063` (accept, BL3006 mismatch, rank-0 + default,
BL3015). #10 and #11 are smaller and can follow.

**#10 and #11 — deferred, handled separately (2026-08-08).** Coordinator
instruction: tuple destructuring (`let a, b = <tuple>`) and tuple lambda
params (`lambda(f, (t, v))`) both sit on top of several muddled ideas in the
parser/pattern code that need untangling before either can be fixed safely.
Out of scope for this fixing round; the analysis above stands, just don't act
on it yet. (Note: parenthesized tuple *destructuring*, `let (a, b) = <tuple>`,
already works today and is unaffected — see `tests/corpus/loops/019_fusion_basic.blade`
line 6 — it's specifically the unparenthesized `let a, b = ...` spelling and
tuple *lambda params* that are blocked.)

### Theme G — documentation (D1–D5)

D1 is the actively misleading one: someone writing a row-wise reduction from the
docs gets a scalar and silently wrong downstream shapes. **FIXED 2026-08-08**
(`docs/features/sql.md` §10). D4 and D5 were framed as "implement or delete"
calls, but on investigation (2026-08-08) they split differently: `blocked<I,
K>` really is described as a built-in and isn't one — confirmed absent from
the parser/lexer entirely (**FIXED**, false claim removed from
`quickstart-2.md`). `<&!>`, on the other hand, turns out to be fully
implemented (lexer, parser, typecheck, ~1000 lines of dedicated codegen) with
21 corpus tests already exercising the literal token — **the "zero corpus
coverage" premise in D5 was simply wrong**, and no doc change was needed
there. D2 and D3 are deferred along with #10/#11 above.

### Suggested order

| Priority | Items | Rationale |
|---|---|---|
| P0 | #1 guard, D1 | Stop the silent wrong answer; stop the misleading doc — **both done 2026-08-08** |
| P1 | #8, #6, #7, #5 | Small, independent, each unblocks real code — **all four done 2026-08-08** |
| P2 | #4 | False rejection that teaches a pessimization — **done 2026-08-08** |
| P3 | Theme A real fix (#1, #2) | The zip-in-a-pack feature, with the duality pin — open; guard makes it safe to defer, and the construction sites are now mapped (see Theme A) |
| P4 | #3 | Deepest; unlocks the natural kernel formulation — open |
| Ongoing | #9, #13, #14 | Fold into whichever pass touches the area — **#12 and D4 done 2026-08-08** |
| **Deferred — handled separately** | **#10, #11, D2, D3** | **Blocked on untangling parser/pattern issues first (coordinator instruction, 2026-08-08); analysis above stands, not actioned this round** |

Every fix above wants a corpus test; per the WARN-pin harness, a warning added or
removed needs its pins updated in the same change.

## 5. Found during the fix round (2026-08-08, final verification)

| # | Sev | Issue | Status |
|---|-----|-------|--------|
| 15 | High | `T<u>^r` param + BARE argument (literal, unannotated binding, bare scalar): passed `blade check`, died BL6001 at IR validation — `unifyParamWithArg` (IR.fs) only saw through unit wrappers when BOTH sides had one, so monomorphization never learned `T -> Float64` | **FIXED** — two asymmetric unit arms; accept-with-lift semantics matching the concrete path. Pin: `units/064_abstract_param_bare_argument` |
| 16 | Med | An **Int64** scalar in an elementwise broadcast inside a function body (`x - reduce(x,(+))/n` with `n = extents(x)`) emits `LOOP_OBJECT_USED_AS_VALUE(x)`; the identical expression with a Float64 scalar (`1.0 * extents(x)`) materializes fine. First real codegen blocker in lsdft's `covariance`, ahead of #3 | **FIXED** — the int-ness was only the trigger (it kept `scalarish` false, so TypeCheck's broadcast re-synthesis skipped and Lowering's direct `IRLet(s, IRApp(IRObjectFor…))` form survived); the real hole was the IR **lift pass**: `isInlineForm` never listed `IRApp(IRObjectFor…)`, so `IRReduce`/`IRProdSum` operand slots kept it inline. New `liftChildIncludingLoopApp` on exactly those two slots (deliberately NOT `liftChildEvaluatedOnce` — hoisting a bare `IRCompute` splits a forced functor map off its loop; see comment at IR.fs:5998). Pin: `functions/055_prodsum_reduce_broadcast_operand` (4 shapes) |
| 17 | Med | `exp <@> (w * ts * ts)` real residue inside a kernel was a silent FALSE ACCEPT (broadcast arm doesn't fire on an unresolved scalar side → element never annotated → nothing to check) | **FIXED** with the Theme C round-2 recheck; pin `unit-errors/004` |
| 18 | High | **Checker/codegen disagreement on mixed real/complex `prodsum` over a generic param**: `prodsum(s, e)` with `s : U^1` and `e` complex UNIFIES `U := Complex` instead of promoting at the site; `blade check` then reports OK for a real-valued caller, but the emitted C++ types the param `Array<complex<double>>` while the call site passes `Array<double>` — a false OK that dies in g++. Correct semantics: mixed prodsum promotes (result complex, operands keep their own elem types), leaving `U` generic. Independent of #3; would block lsdft even after #3 lands (workaround: declare the series complex) | Open — repro is `lsdft` + any real-valued driver; check-time soundness gap makes this High |

### lsdft end-to-end status (the motivating program)

- Parse + full front-end type check: **OK** (needed #8, #4 round 1+2, #15).
- `covariance` and the `s = s_raw - reduce(s_raw,(+))/n` chain now compile
  (#16 fixed; zero LOOP_OBJECT sentinels from those sites).
- Remaining blockers, all inside the `omegas <@> lambda(w) -> {…}` kernel and
  all pre-existing **#3 proper**: (a) `let e = exp <@> (i * w * ts)` is
  re-materialized in the lifted kernel under a fresh id with the `exp`
  wrapper lost (`'__v27' was not declared`); (b) the function's return
  position holds the bare apply (`UNEVALUATED_COMPUTATION_USED_AS_VALUE`);
  (c) issue #18 above — the mixed real/complex prodsum unification seam, now
  characterized: NOT a promote-at-codegen problem but a check-time false OK.
  Also confirmed en route: the `Unit time: day` quantity ascriptions demand
  `: time` / `: angular_frequency` ascriptions at call sites (BL3010) — an
  intentional strictness feature, satisfied by ascription, not a bug.
  The program is the natural conversion target and acceptance test for #3's
  design pass, with #18 fixed alongside.
