# Tuples vs argument packs: what Blade actually does, and whether both can coexist

Written 2026-08-08, answering: *"tuples were explicit, but that was somewhat
rolled back to support a cleaner API. However, that may lose some
distinctiveness and cause major bugs if we can't decide between an arg pack and
a tuple. Research how it's currently implemented and how we can, hopefully, have
both. If it's not possible, we'll revert to explicit tuples everywhere."*

Every behavioral claim below was **compiled and run** against `master` +
today's uncommitted `src/` changes with a private build
(`C:\bt_tup\bin\Blade.exe`), not read off the source. Repros are inlined so this
document survives the scratch files.

**On the citations.** File:line was taken with `grep -n` (the graft index is
empty for this checkout, and the agent Read/Grep tools report line numbers
offset by ~158 in `TypeCheck.fs` — do not trust them here). `src/TypeCheck.fs`
was being edited by another agent while this was written and drifted ~11 lines
mid-research; **every `TypeCheck.fs` number below was re-derived in one atomic
pass** against `f1ba7b2` + working tree, `md5 9b053b1af319f77ae6d1f2fb50e68eca`.
Each citation also names its symbol, which is the durable anchor if the numbers
drift again.

**Headline.** The pack-vs-tuple decision today is made **syntactically, at three
sites, at three different alias depths**, and never from the type. That is
already producing check-time false OKs that die in `g++`, with no tuple feature
involved. The `Poly<T^k>` path, meanwhile, is a *working* coexistence rule that
already ships. Recommendation is coexistence — see §7.

---

## 1. Motivation

Two designs are live in the tree at once.

**The explicit-tuple design** is what the docs and the formalism describe. The
operand list of a loop *is* a tuple, and a kernel is a function *of that tuple*:

- `formalism.md:131` — "`zip(A,B)(i..) = Tuple(A(i..), B(i..))`; **kernel
  receives ONE tuple argument**"
- `formalism.md:787` — "**Nested tuples preserve structure** (`arity` counts top
  level; `comm` does not penetrate sub-tuples; no deep indexing — destructure
  instead)"
- `formalism.md:1275` — "Tuples: `(a, b)` literals; ... `(e)` is grouping, not a
  1-tuple"
- `formalism.md:1336` — "`()` … **empty array tuple** (identity for `<*>`)"
- `quickstart-2.md:105` — "The zero **array tuple** `()` is the empty **argument
  pack**" (the two words used as synonyms, in one sentence)
- `quickstart-1.md:207` — the discriminating pair, verbatim:
  ```F#
  let add((a: T^0, b: T^0)) = a + b
  method_for(zip(A, B)) <@> add   // A + B   (zip first: elementwise)

  let add(a: T^0, b: T^0) = a + b
  method_for(A, B) <@> add        // A [+] B (outer-product style)
  ```
  Under that design the **kernel's parameter shape** carries the distinction: a
  1-param tuple kernel for the zip, a 2-param kernel for the outer product.

**The flattened design** is what the compiler implements. `zip` is dissolved at
the loop former into `k` independent operands; every operand contributes exactly
one kernel parameter; `lambda((a, b))` does not parse at all. The distinction is
carried entirely by the **loop former** (`zip(...)` vs `,`), and the kernel is
identical in both of quickstart-1's lines.

There was no code rollback to find. `inferZip` computes the tuple element type
and **throws it away** — and has since the arm was written
(`340be98`, 2026-02-21, "reynolds operators; value testing"; the discard is in
the introducing diff):

```fsharp
// TypeCheck.fs:5978-5985
let tupleElemType =
    match elemTypes with
    | [single] -> single  // degenerate: single-array zip
    | _ -> IRTTuple elemTypes
// Infer a shared ElemType tag for the IRArrayType wrapper
// We use ETFloat64 as placeholder since the real element is a tuple
let zipArrayType =
    mkArrayArrow sharedIndices (IRTScalar ETFloat64) None  // placeholder; real elem is the tuple
```

`tupleElemType` is bound and never referenced. The explicit-tuple design lived in
the docs and in the *comments*; it was never wired into the type. So the question
is not "restore the old thing" — it is "pick one, and make the seams agree".

---

## 2. Current-state map

### 2.1 Every parse of a parenthesized comma list

| Site | `file:line` | Produces | Reading |
|---|---|---|---|
| `f(a, b)` postfix call | `Parser.fs:1859-1861` | `ExprApp (f, [a; b])` | **arg list** (never a tuple) |
| `t[k]` postfix | `Parser.fs:1862-1866` | `ExprTupleIndex` | tuple/poly projection |
| `method_for(A, B)` | `Parser.fs:2498-2502` | `ExprMethodFor [A; B]` | **operand pack** (`Expr list`, no tuple node) |
| `for (A, B) [in v]` | `Parser.fs:2507-2519` | `ExprFor (ForArrays ([A;B], _), …)` | **operand pack** (`Ast.fs:515`) |
| `zip(A, B)` | `Parser.fs:1970-1974` | `ExprZip [A; B]` | **co-iteration pack** |
| `stack` / `join` / `sequence` | `Parser.fs:1976-2003` | own nodes | packs |
| `lambda(a, b)` / `function f(a, b)` | `Parser.fs:2274, 2698, 2910` | `LambdaParam list` / `ParamDecl list` | **param list**; each entry must be a bare `expectIdent` (`Parser.fs:2306`) |
| guard call `p(a, b)` | `Parser.fs:2477-2480` | `ExprApp` | arg list |
| **`(e1, e2, …)` in expression position** | `Parser.fs:2560-2600` | **`ExprTuple`** | **tuple value** — the only producer |
| `(e)` single element | `Parser.fs:2581, 2593` | `e` itself | grouping (matches `formalism.md:1275`) |
| `(e1, e2)` in a *static payload* slot | `Parser.fs:474-485` | `ExprTuple` | tuple value |
| `(T1, T2)` in type position | `Parser.fs:793-798` | **`TyTuple`** (`Ast.fs:201`) | tuple type; `(T)` collapses |
| `(p1, p2)` in pattern position | `Parser.fs:1245-1251` | **`PatTuple`** (`Ast.fs:374`) | tuple pattern; `(p)` collapses |

So at the **parser** level the two ideas are already separate: formers take
`Expr list`, only `parseParenExpr` mints `ExprTuple`. **`f(a, b)` and
`f((a, b))` are distinguishable** — the second is `ExprApp (f, [ExprTuple […]])`.

### 2.2 Where a tuple gets *splattered* into a pack

Three sites, three different alias depths:

| Site | `file:line` | Matches on | Alias depth |
|---|---|---|---|
| `<@>` LEFT operand, implicit former | `TypeCheck.fs:6193-6198` | untyped AST `ExprKind.ExprTuple` | **0 hops** |
| `<@>` RIGHT operand, `object_for(k)` | `TypeCheck.fs:7818-7820` | `rR.Kind = TExprTuple` after `resolveTypedExpr` | **1 hop** |
| `<@>` RIGHT, `object_for(<combinator>)` | `TypeCheck.fs:7743-7745` | same | 1 hop |
| `<@>` RIGHT, compose chain `>>@` | `TypeCheck.fs:8038-8040` | same | 1 hop |

`resolveTypedExpr` (`TypeCheck.fs:7467-7473`) follows a `TExprVar` to its
`TypedValue` **exactly once**, then stops. That single fact is responsible for
most of §3.

There is **no** tuple↔pack rule in unification: `Unify.fs:710` unifies
`IRTTuple` only against an equal-length `IRTTuple`, and `Unify.fs:727` unifies
`IRTPoly` only against `IRTPoly`. So the splat is not a coercion the type system
can see, sanction, or diagnose — it is an AST rewrite that happens before typing.

### 2.3 Tuple VALUES: producers and consumers

Producers (all verified running):
- `(a, b)` literal → `TExprTuple` (`TypeCheck.fs:4022-4024`) → `IRTuple`
  (`Lowering.fs:340-341`) → `std::make_tuple` (`CodeGen.fs:2104`).
- `<&!>` / `<&>` fusion → `IRTTuple [l; r]` (the `OpParallel | OpFusion` arm in
  `inferApply`'s combinator fold). Real, first-class — this runs and prints
  `[11, 12, 13]`:
  ```blade
  function pick(p: (Array<Float64 like Idx<3>>, Array<Float64 like Idx<3>>))
      -> Array<Float64 like Idx<3>> = p[0]
  let A = [1.0, 2.0, 3.0]
  let L = method_for(A)
  let both = (L <@> lambda(x) -> x + 10.0) <&!> (L <@> lambda(x) -> x * 2.0) |> compute
  let s = pick(both)
  ```
- A kernel returning a tuple → array of tuples
  (`tests/corpus/loops/070_kernel_returns_tuple.blade`, passes).
- `zip` in *expression* position → `std::make_tuple` (`CodeGen.fs:2365-2367`).
  This is the only place a zip element becomes a tuple; at a loop former it never
  does.
- `IRApp(IRObjectFor k, [IRTuple [A; B]])` — the **operand pack itself is an
  `IRTuple` at IR level** (`Lowering.fs:1115`). Pack and tuple share the node.

Consumers, all working:
- `let (a, b) = t` — `PatTuple`, and it destructures **flat**: `let (a,b,c) = y`
  with `y = ((1,2),3)` binds `1,2,3`
  (`tests/corpus/basic/024_flat_tuple_destructure_expr_position.blade`, pinned).
  Tuple patterns in Blade **already flatten across nesting**.
- `let a, b = t` — **does not parse** (`parseLet` → `parsePattern` sees `a`, then
  expects `:`/`=` and finds `,`). Issue #10.
- `t[0]` → `TExprTupleIndex` → `std::get<0>`.
- A declared tuple param: `function f(p: (Float64, Float64))` — works, including
  `let (x, y) = p` inside the body
  (`tests/corpus/func-arrays/008_fa_t8_tuple_of_arrays_param.blade`).
- `Array<(Float64, Float64) like Idx<2>>` — type-checks and runs.
- `head :: tail` on a `Poly` pack (`TypeCheck.fs` cons-destructure arm; corpus
  `arity/022`, `arity/025`).

Non-consumers:
- **Lambda params cannot be patterns.** `LambdaParam.Name : Ident`
  (`Ast.fs:518-519`) and `parseLambdaParam` opens with `expectIdent`
  (`Parser.fs:2306`). `lambda(f, (t, v))` is `BL1001`. Issue #11.

### 2.4 The kernel-param seam

`buildApplyInfo` (`TypeCheck.fs:8095`) is where a pack becomes kernel params.

1. `kernelInputRanks` (`TypeCheck.fs:8519-8547`) — per-param slice rank.
2. `expandedRows` (`TypeCheck.fs:8707-8719`) — **one row per operand ARRAY**
   (a virtual `range<I,J>` source expands to one row per rank slot). This is the
   flattening. There is no path by which `k` rows become one tuple.
3. `kernelParamUnifyResult` (`TypeCheck.fs:8756-8768`):

```fsharp
if lambdaInfo.Params.Length = expandedRows.Length then
    (List.zip resolvedParamTypes expandedRows) |> … unify …
else
    Ok ()  // Arity mismatch handled elsewhere; don't double-report.
```

**"Elsewhere" does not exist.** The only kernel-arity check in the tree is on the
`for (…) in …` co-iteration path (`TypeCheck.fs:11746-11748`). Every `<@>` with a
kernel arity ≠ pack width silently skips unification — §3.4.

`zip` never reaches this seam intact. `inferMethodFor`'s zip arm
(`TypeCheck.fs:11080-11113`) sets `Arrays = <the k children>` and
`Arity = Some k`; `inferApply`'s `object_for` arm splices the children into
`flatArrays` (`TypeCheck.fs:7846-7854`). The `isCoIterGroup` list it builds —
which is exactly the structure `(A, zip(B,C))` needs — is used only to test
`allCoIter` (`TypeCheck.fs:7859`) and then dropped.

### 2.5 The one place coexistence already works: `Poly<T^k>`

`object_for(comoment) <@> (data, data)` with `comoment(a: Poly<T^1>)` — one
declared param, two operands — is a **shipping, pinned** feature
(`tests/corpus/arity/022`, `025`, `027`, `028`). The mechanism is the
deferred-former arm (`TypeCheck.fs:7944-7963`): the pack width is read off the
operand list at the apply, the kernel is eta-expanded to that width
(`lambda(__of0, __of1) -> comoment(__of0, __of1)`), and the callee's `Poly` param
re-absorbs the arguments at the *call*. The comment at
`TypeCheck.fs:6166-6169` calls the operand list "the argument tuple".

Verified both orientations:

```blade
function psum(args: Poly<T^0>) -> T^0 = args[0] + args[1]
object_for(psum) <@> (A, B)      // 2x3 outer product   [[11,21,31],[12,22,32]]  OK
object_for(psum) <@> zip(A, B)   // co-iteration        [11,22,33]               OK
```

So the language **already dispatches pack-vs-whole on the kernel's declared
parameter type**, and it works. What is missing is the *fixed-arity* case: the
identical program with `p: (Float64, Float64)` instead of `Poly<T^0>`
miscompiles (§3.6).

---

## 3. Muddle inventory

Ten collision cases. Every one of them **passes `blade check`**. `Sev` is by
consequence: *silent wrong answer* > *g++ failure* > *parse error*.

| # | Sev | Shape | Which design it assumes | What actually happens |
|---|---|---|---|---|
| M1 | **Critical** | one alias hop before `<@>` | pack | works at 1 hop, miscompiles at 2 |
| M2 | **Critical** | kernel arity ≠ pack width | flattened | silently drops operands, or dies in g++ |
| M3 | **Critical** | 1-param kernel over a `zip` of 2 | explicit-tuple (docs) | silently drops the 2nd array |
| M4 | High | `(A, B)` on the LEFT of `<@>`, let-bound | pack | miscompiles (0 alias hops) |
| M5 | High | `method_for((A, B))` | pack | miscompiles |
| M6 | High | tuple-annotated kernel param | explicit-tuple (docs) | miscompiles |
| M7 | High | a real tuple value fed to `<@>` | — | miscompiles |
| M8 | Med | nested pack `(A, (B, C))` | explicit-tuple (`formalism:787`) | miscompiles |
| M9 | Med | tuple arg to a `Poly` pack param | either | miscompiles / `BL6001` |
| M10 | Low | `lambda((a,b))`, `let a, b =` | explicit-tuple (docs) | `BL1001` (#10, #11) |

### 3.1 M1 — the alias-hop counterexample (**the single most valuable artifact**)

```blade
let A: Array<Float64 like Idx<2>> = [1.0, 2.0]
let B: Array<Float64 like Idx<3>> = [10.0, 20.0, 30.0]
let P = (A, B)
let Q = P
let K = object_for(lambda(a, b) -> a * b)
let g = K <@> P |> compute   // 2x3 outer product: [[10,20,30],[20,40,60]]
let h = K <@> Q |> compute   // error: '__v6' was not declared in this scope
```

`blade check` → **OK**. `blade run` → g++ failure on `h`.

`P` and `Q` have the **same type** and the **same value**. `resolveTypedExpr`
(`TypeCheck.fs:7467`) takes exactly one hop, so `P` resolves to `TExprTuple` and
splats; `Q` resolves to `TExprVar "P"` and is treated as a single array operand.
This is the "major bug" class, and it needs no new feature to exist — it is
today's behavior.

### 3.2 M4 — the left-hand mirror, at depth **zero**

```blade
let g = (A, B) <@> lambda(x, y) -> x * y |> compute   // BL4004 note; runs, 2x3
let P = (A, B)
let h = P <@> lambda(x, y) -> x * y |> compute        // 'std::tuple<…>' has no member named 'extents'
```

The left-side arm matches the **untyped** `ExprKind.ExprTuple`
(`TypeCheck.fs:6193`), so it does not follow bindings at all. The two operands
of one operator therefore disagree about how far to look through a `let`.

### 3.3 M5, M7 — a tuple in operand position

```blade
let g = method_for((A, B)) <@> lambda(a, b) -> a * b |> compute
// check OK; g++: '__v4' was not declared in this scope
```

`parseMethodFor` (`Parser.fs:2500`) parses its own comma list, so the inner
parens make **one** operand whose type is `IRTTuple` — and `inferMethodFor`'s
general arm treats it as an array. One pair of parentheses, silently different
program.

Same failure for a genuine fusion tuple:

```blade
let both = (L <@> lambda(x) -> x + 10.0) <&!> (L <@> lambda(x) -> x * 2.0) |> compute
let g = object_for(lambda(a, b) -> a * b) <@> both |> compute   // check OK; g++ fails
```

`both` is a *bona fide* `IRTTuple` value (it passes into a declared tuple param
correctly — §2.3). It is only in operand position that it has no meaning.

### 3.4 M2 — kernel arity is never checked at `<@>` (silent wrong answers)

**Under-arity — runs, wrong:**

```blade
let A: Array<Float64 like Idx<2>> = [1.0, 2.0]
let B: Array<Float64 like Idx<3>> = [10.0, 20.0, 30.0]
let C: Array<Float64 like Idx<2>> = [5.0, 6.0]
let g = object_for(lambda(a, b) -> a + b) <@> (A, B, C) |> compute
// g = [11,11,21,21,31,31,12,12,22,22,32,32]   -- 12 cells, C's axis iterated and ignored
```

No diagnostic at any stage. Sections do the same:
`method_for(A, B, C) <@> (+)` gives the identical 12 cells.

**Over-arity — check OK, g++ fails:**

```blade
let g = object_for(lambda(a, b, c) -> a + b + c) <@> (A, B) |> compute
// error: '__v6' was not declared in this scope
```

Both are `TypeCheck.fs:8768` (`else Ok () // Arity mismatch handled
elsewhere`) doing nothing.

### 3.5 M3 — the exact program that would distinguish the two designs

```blade
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0, 30.0]
let g = method_for(zip(A, B)) <@> lambda(p) -> p * 2.0 |> compute
// g = [2, 4, 6]   -- B silently dropped
```

- The **docs** (`quickstart-1.md:207`, `formalism.md:131`) say `p` is the tuple
  `(a, b)`, so this is a type error (no `*` on a tuple).
- The **flattened design** says the kernel needs 2 params, so this is an arity
  error.
- The **compiler** does a third thing: binds `p` to A's element, iterates the
  co-iteration axis, and returns a plausible-looking array.

This is the case the language owner is worried about, and it is live.

### 3.6 M6 — a fixed-arity tuple param as a kernel

```blade
function addp(p: (Float64, Float64)) -> Float64 = p[0] + p[1]
let g = method_for(zip(A, B)) <@> addp |> compute
// g++: could not convert '__fp_A[__fk]' from 'const double' to 'std::tuple<double, double>'
```

The documented spelling, with the tuple made explicit in the *type* rather than
in an unparseable pattern, still splats. Contrast §2.5: the identical program
with `Poly<T^0>` works. **The dispatch exists; it is just keyed on `IRTPoly`
only.**

### 3.7 M8 — nested packs

```blade
let g = object_for(lambda(a, b, c) -> a + b + c) <@> (A, (B, C)) |> compute
// check OK; g++: '__v7' was not declared in this scope
```

The splat is one level deep, so `(B, C)` becomes an "array" operand. There is no
spelling for a nested pack, even though `formalism.md:787` promises "nested
tuples preserve structure". Note also that `((A, B))` ≡ `(A, B)` — the
single-element collapse at `Parser.fs:2581/2593` means **extra parentheses can
never mark a tuple**. Verified: `K <@> ((A, B))` gives the same 2×3 grid as
`K <@> (A, B)`.

### 3.8 M9 — a tuple argument to a `Poly` pack

```blade
function polySum(args: Poly<T^0>) -> T^0 = args[0] + args[1]
let t = (3.0, 4.0)
let b = polySum(t)      // monomorphizes as …_tup_double_double(std::tuple<double,double>)
                        // then: '__v2' was not declared in this scope
let c = polySum((3.0, 4.0))   // BL6001: unresolved type variable T?10000; dangling VarId v5
```

`Poly<T^k>` *is* "a tuple of statically-unknown arity" — `args[k]`, `arity(args)`
and `head :: tail` are all tuple operations. Passing it a tuple is the one place
the two concepts are genuinely the same thing, and the compiler produces two
different kinds of broken output depending on whether the tuple is named.

### 3.9 Two neighbours that are *not* tuple bugs (recorded so they aren't blamed)

- `f((1.0, 2.0))` on a 2-param `f` passes `check` and emits
  `f(std::make_tuple(1.0, 2.0), __pa3_0)`. It is **not** a splat — it is a legal
  *partial application* (`f(1.0)` then `g(2.0)` works), and the eta-expansion
  path simply never type-checks its argument. `f("hello", "world")` against
  `Float` params passes `check` too. This is a general argument-checking hole at
  the currying seam, orthogonal to tuples — but it is why "let the arity error
  tell us which reading was meant" is not a mechanism you can build on today
  (§5.1).
- A 2-param kernel over a **real** array of pair elements miscompiles, so the
  flattening is a property of `zip`'s **syntax**, not of tuple-element arrays —
  the two are not interchangeable in either direction:
  ```blade
  type NIdx = Idx<3>
  let xs: Array<Float64 like NIdx> = [1.0, 2.0, 3.0]
  let T = method_for(xs) <@> lambda(x) -> (x, x * 10.0) |> compute   // Array<(Float,Float)>
  let g = method_for(T) <@> lambda(a, b) -> a + b |> compute
  // check OK; g++: '__v7' was not declared in this scope
  ```
  The 1-param spelling (`lambda(p) -> p[0] + p[1]`) gets the *body* right
  (`std::get<0>(…) + std::get<1>(…)`) but leaves the output array's element type
  as `tuple<double,double>` — a separate return-type-adoption bug, noted here so
  it isn't rediscovered as a tuple-dispatch bug.

### 3.10 Where the two readings are *deliberately* different, and correct

- `object_for(<@>) <@> ((L1, f1), (L2, f2))` requires **nested** tuples: the arm
  at `TypeCheck.fs:7750-7756` reads each pack element as a `(loop, kernel)`
  pair. Verified running. Any rule that outlaws tuples in operand position must
  exempt this one.
- SparseIdx / compound subscripts are the one *argument* position where `(a, b)`
  is genuinely one tuple value (`formalism.md:191`); `TypeCheck.fs:2131` records
  that `CompoundIdx` was moved off that spelling onto flat subscripts and
  `SparseIdx` inherited it.

---

## 4. Design A — coexistence (rule **R**)

The claim of this design is that the distinction is **already decidable**, from
the kernel's *declared parameter shape*, and that the Poly path proves it.

### R1 — Pack width is structural, and identical on both sides of `<@>`

A parenthesized list **written at an operand position** is a pack of its
elements. A **variable is always exactly one operand**, whatever it is bound to.
Delete the `resolveTypedExpr`-then-match-`TExprTuple` splat at
`TypeCheck.fs:7818`, `7743`, `8038`; keep the syntactic match at `6193`.

This single change kills M1 and M4 and makes the operator's two sides agree. It
is required **under either design** and should land first, independently.

### R2 — A tuple-typed value in operand position is a hard error

`IRTTuple` (or an unresolved tuple) reaching `method_for` / `<@>` operand
position gets a diagnostic, not a `std::tuple::extents` call: *"a tuple value is
not an array; write its components as separate operands, or project it with
`t[0]`"*. Replaces the five miscompiles M1/M4/M5/M7/M8 with one message.
Exempt the `object_for(<@>)` pair-list arm (`TypeCheck.fs:7750-7756`).

### R3 — Kernel dispatch, by declared param shape

At `buildApplyInfo`, with `rows = expandedRows` and kernel params `q_1..q_m`
(`m_req` = params before the first default, `m` = total):

| condition | mode | mechanism |
|---|---|---|
| `m_req ≤ |rows| ≤ m` | **flattened** (today) | `TypeCheck.fs:8760-8766`, unchanged |
| `m = 1`, `q_1 : Poly<T^k>` | **pack** | today's deferred former, `TypeCheck.fs:7944-7963` |
| `m = 1`, `q_1` is a **tuple pattern** of width `|rows|` (`lambda((a,b))`), or is annotated `TyTuple` of width `|rows|` | **tuple** *(new)* | same eta-expansion, but the body call is `f((__of0, …, __ofn))` |
| `|rows| = m_req + idxSlots` | **flattened + indices** | today's `for … in` rule, `TypeCheck.fs:11746-11748` |
| `m = 1`, `q_1` a bare **unannotated name**, `|rows| > 1` | **hard error** | new BL3xxx, §5.2 |
| otherwise | **hard error** `ArityMismatch` | the check `8768` promises |

Note both tuple triggers are **written syntax** — a pattern or an annotation —
never an inferred type. That is what §5.1 forces.

The new tuple arm is ~15 lines: it is the Poly arm with a fixed width and one
`ExprTuple` wrapper around the eta-expanded argument list.

### R4 — `zip` keeps dissolving, but carries its group tag

Keep `zip(B, C)` contributing **two rows** (the whole corpus depends on it: 36
files), but stop discarding `isCoIterGroup` (`TypeCheck.fs:7846-7859`). A pack
`(A, zip(B, C))` becomes rows `[a; b; c]` with groups `[{0}, {1,2}]`, shared
records computed *within* group 2 only. That is precisely the Theme A feature
in `plan-array-expression-fixes.md` §4, and it makes today's BL3999 guard
(`TypeCheck.fs:2830`, `7840`, `11157`) removable rather than permanent.

Consequence for the docs: `lambda((a, b))` and `lambda(a, b)` become the **same
kernel** over a 2-zip, which is what quickstart-1's two lines want to be able to
say, and R3 makes the tuple spelling legal.

### R5 — Fix the surface holes that R3 depends on

- `lambda((a, b))` must parse (#11). `LambdaParam.Name : Ident`
  (`Ast.fs:518-519`) becomes a `Pattern`, or a sibling `PatParam` case; every consumer of
  `LambdaParam.Name` in `TypeCheck`/`Lowering`/`Deduce`/the ML walkers must
  handle the pattern case. **This is the largest single piece of work in Design
  A** and is the reason #11 was deferred.
- `let a, b = t` (#10) is independent of everything here — the parenthesized form
  already works and is what the corpus uses (87 files). It can be done or
  dropped on its own merits.

---

## 5. Failure modes of Design A

### 5.1 Arity-directed elaboration cannot be *inferential*

This is the real constraint, and it rules out the more ambitious version of the
rule ("try the tuple reading; if it doesn't type, use the pack reading").

**Kernel bodies are inferred before their parameters are bound.** `inferLambda`
types the body against fresh vars, and `buildApplyInfo` unifies the params
against `expandedRows` only afterwards (`TypeCheck.fs:8756`). Theme C of
`plan-array-expression-fixes.md` documents the same ordering from the units side
("the body was inferred against unresolved vars"). So at the moment the pack/
tuple choice must be made, `lambda(p) -> p * 2.0` looks exactly like
`lambda(p) -> p[0] + p[1]`. **The body cannot vote.**

Nor can a failed attempt: §3.9 shows the call seam accepts a `std::string` where
a `Float` was declared, so "did it type?" is not a reliable oracle at all.

Therefore R3 must dispatch on **declared syntax only** — the param's written
type annotation and the param count. Anything else is guesswork.

### 5.2 The one genuinely undecidable shape

`m = 1`, the param is unannotated, and `|rows| > 1`:

```blade
let g = method_for(zip(A, B)) <@> lambda(p) -> <body> |> compute
```

No local rule can choose. The pack reading and the tuple reading are both
well-formed, they disagree on the answer, and (per 5.1) the body is not
available as evidence. Design A's answer is a **hard error demanding an
annotation** — which is strictly better than today's silent wrong answer (M3),
and is the same discipline `Float<speed>` quantity params already impose
(BL3010).

Honest statement: this is the only shape I found where two readings both type
and disagree. I did **not** find a case where an annotation cannot resolve it.

### 5.3 The rest of the seam list

| Seam | Under R |
|---|---|
| Sections `(+)` | synthesize a 2-param lambda; land in flattened mode; **now error** at `|rows| ≠ 2` instead of silently dropping operands (measured today: `method_for(A,B,C) <@> (+)` → 12 wrong cells) |
| Defaulted kernel params | `m_req ≤ |rows| ≤ m`. Verified working today: `lambda(a, b, c = 1.0)` over a 2-pack fills `c` |
| `reynolds(k)` | dispatch on the inner kernel; `TypeCheck.fs`'s reynolds arms already recurse |
| `for (…) in range<…>` index params | keep the `n` or `n + R` rule (`TypeCheck.fs:11746-11748`) as an explicit extra row in R3 |
| Poly + tuple both declared | no `IRTPoly ~ IRTTuple` rule exists in `Unify.fs`; a `Poly` param never matches the tuple arm, and vice versa. Disjoint by construction |
| `<*>` array product | concatenates loop operand lists; unaffected (20 corpus files) |
| Single-array `zip(A)` | `inferZip` falls to the `IRTTuple [t]` fallback (`TypeCheck.fs:5959`, `>= 2` guard), but `inferMethodFor`'s syntactic zip arm (`TypeCheck.fs:11080`) catches it first. Verified running. R must not disturb the syntactic arm |
| Nested zip `zip(zip(A,B), C)` | miscompiles today (`arr0` undeclared). R4's group tags give it a meaning; until then it should join the BL3999 guard |

### 5.4 What Design A does not fix

The currying/argument-checking hole (§3.9). It is orthogonal but it *looks* like
a tuple bug (`f((a, b))` producing `std::make_tuple`), so it will keep being
reported as one until it is closed.

---

## 6. Design B — explicit tuples everywhere

### What it means, precisely

`(e1, …, en)` denotes a tuple value **in every position**, including operand
position. Loop formers take a tuple. Kernels take a tuple. Concretely:

1. `<@>`'s RHS is one value. `object_for(k) <@> (A, B)` is unchanged *in
   spelling* but now means "apply `k` to the tuple `(A, B)`" — and `K <@> P`
   with `let P = (A, B)` means the **same thing**, which is the point.
2. Kernels become 1-param: `lambda((a, b)) -> a + b`. **#11 is a hard
   prerequisite** — the design is unwritable until lambda params can be
   patterns.
3. `method_for(A, B)` either keeps its own comma list (`Parser.fs:2500`, not a
   tuple today) or becomes `method_for((A, B))`. Keeping it is inconsistent;
   changing it touches 229 corpus files.
4. `Poly<T^k>` becomes "tuple of statically-unknown arity", and `arity`, `comm`,
   `args[k]`, `head :: tail` must work on ordinary tuples. §3.8's M9 stops being
   an ambiguity and becomes the main path.
5. `zip(A, B)` finally has an element type: the `IRTTuple` that
   `TypeCheck.fs:5978` computes and discards. `expandedRows` collapses to one row
   per operand, period.

### Measured migration cost

Corpus (1398 `.blade` files):

| spelling | files |
|---|---|
| `method_for(…, …)` multi-operand | **229** |
| `<@> (a, b)` pack | 33 |
| `zip(…)` | 36 |
| `for (A, B)` sugar | 7 |
| `<*>` | 20 |
| `<&!>` | 21 |
| `let (a, b) = …` | 87 |

Essentially every multi-operand kernel in the corpus changes shape
(`lambda(a, b)` → `lambda((a, b))`), plus every doc example that uses the
flattened form. The docs *get cheaper*: `quickstart-1.md:207`,
`quickstart-2.md:256`, `formalism.md:131/787/1275/1336` already describe B, so
D2/D3 in `plan-array-expression-fixes.md` close by making the compiler right
rather than the docs wrong.

### What B buys and what it doesn't

**Buys:** one concept. No dispatch table. The M1/M4 alias asymmetry evaporates
because a variable and a literal denote the same thing. `formalism.md:787`'s
nested-structure promise becomes implementable. `zip` gets an honest type.

**Does not buy:** any of §3's fixes come free. Under B, *every* one of M1–M8 is
the main path and must be implemented — a tuple in operand position stops being
an error case and becomes the only case, so `method_for` must handle tuple
operands, `expandedRows` must destructure them, and codegen must not call
`.extents` on a `std::tuple`. **B front-loads the work rather than avoiding it.**

**Costs:** it deprecates `lambda(a, b)`, the most common kernel spelling in the
language and the one the "cleaner API" was adopted for; it makes the outer
product `method_for(A, B) <@> lambda((a, b)) -> …` read worse than the
elementwise case rather than better; and it requires #11 before line one.

---

## 6b. Design C — flat normal form + width schemas (owner's sketch, 2026-08-08)

Added after review. The owner's framing of the hazard is sharper than §5's: if
flattening is an **equation** — `f(a, (b, c)) == f(a, b, c)` — then grouping is
destroyed at the call site by definition, so no operand-side rule can ever
recover it; the recovery must come from the **signature side**. Sketch:

- Packs have a flat normal form: inner parens in operand position carry no
  meaning. The equation holds everywhere.
- An explicit `Tuple<N>` type exists for annotations, and the bare-comma
  binding constructs it: `let t = b, c` has type `Tuple<2>` (this makes #10's
  unparenthesized form a **prerequisite**, not an option).
- Kernel (and function) parameter lists are **width schemas** over the flat
  pack: an unannotated param consumes one slot; `y: Tuple<2>` consumes two
  scalar slots *or* one already-tuple-typed operand. Sum of widths ≠ pack
  width is a hard error. So `f(a, (b,c)) <@> lambda(x, y: Tuple<2>)` and
  `f(a, b, c) <@> lambda(x, y: Tuple<2>)` both slice 1+2, and
  `lambda(x, y)` over either is an **arity error** rather than today's silent
  operand drop (M2).

Assessment against this document's findings:

- **Subsumes R3.** The dispatch table's flattened mode (all widths 1), the
  Poly whole-pack mode (one param, width = pack), and the tuple arm are all
  instances of one width-schema rule; mixed signatures come free. M2 and M8
  are fixed by construction; M6 becomes the main path.
- **M1 (alias asymmetry) is solved by slot-matching + substitution:**
  `let t = b, c; f(a, t)` matches `y: Tuple<2>` by t's *static* type, so it
  equals `f(a, (b, c))` — no splat/no-splat divergence. This holds ONLY under
  a hard discipline: **tuple-ness is always written, never inferred** (a
  tuple-typed param must carry `Tuple<N>`; a tuple value is only born from an
  explicit comma construction). If tuple-ness could be an inference variable,
  pack widths become inference-dependent and §5.1's cliff returns. Precedent:
  quantity params already impose exactly this (BL3010).
- **Depth ruling required:** recommend the free-monoid reading — packs deep-
  flatten, widths count leaves, `Tuple<N>` annotations re-nest. `((a,b),(c,d))`
  is width 4; `lambda(p: Tuple<2>, q: Tuple<2>)` slices it; `lambda(r:
  Tuple<4>)` also legal.
- **Element-level symmetry, for free:** the same annotation reads one level
  down — `method_for(zip(B, C)) <@> lambda(p: Tuple<2>)` gives `p` the pair
  per iteration. That is quickstart-1 §5's promised "kernel receives ONE tuple
  argument", recovered as an opt-in spelling, and §5.2's undecidable shape
  becomes the same annotation-required error as in rule R.
- **Costs:** #10 (bare comma) becomes load-bearing; #11 (tuple patterns) is
  wanted for destructuring the annotated param but is not a hard blocker
  (indexing/projection suffices initially); `Tuple<N>` is width-only —
  element types stay inferred (a full `(T1, T2)` spelling can coexist later).

Status: **ADOPTED — owner ruling, 2026-08-08.** The rulings:

1. `Tuple<>` stays **explicit** (mandatory annotation on tuple params; no
   inferred tuple-ness). Confirmed.
2. Depth: the **free-monoid reading** is confirmed — packs deep-flatten,
   widths count leaves, `Tuple<N>` annotations re-nest. `((a,b),(c,d))` is
   width 4; `lambda(p: Tuple<2>, q: Tuple<2>)` slices it; `lambda(r: Tuple<4>)`
   also legal.
3. A literal `(a, b)` **as an argument is a tuple value** — it needn't be
   let-bound; it works the same way as a bound `Tuple<2>` (by the leaf/width
   arithmetic the two readings coincide).
4. The currying/argument-checking hole (§3.9) is to be **fixed as its own
   item** alongside this design — direct calls must type-check their
   arguments at check time.

Implementation consequence: §8's staged plan survives nearly intact — R1/R2
land as bugfixes, R3 is replaced by the width-schema matcher, and the Tuple<N>
surface (type + bare-comma let construction + literal tuple arguments) is its
own stage. The canonical matcher: compute the pack's flat LEAF sequence
(deep-flatten written parens AND tuple-typed values by their static component
widths); kernel/function schema widths are unannotated = 1, `Tuple<k>` = k;
total leaves must equal total width (hard error otherwise); each `Tuple<k>`
param takes its k consecutive leaves as a tuple. Substitution holds by
construction: `f(a, t)` and `f(a, (b, c))` and `f(a, b, c)` produce the same
leaf sequence.

## 6c. Revision under discussion (owner, 2026-08-08, post-implementation):
one-level structural matching

The deep-flatten ruling in §6b overshot the owner's original formulation. The
owner asked for *conditional* equivalence — `f(a,(b,c)) == f(a,b,c)` **when**
the schema licenses it (`lambda(x, y: Tuple<2>)`) — and for `f((a,b))` to drop
redundant parens. Deep-flatten made the equation *unconditional*, which costs
real functionality: nested tuples stop being data, `Poly<T^k>`'s documented
structure-preservation (arity counts top level, comm does not penetrate
sub-tuples) is measurably broken (§9), and parens can never disambiguate.

Proposed replacement — matching on the TOP-LEVEL SPINE, nesting preserved:

1. Each pack/argument element (written group or tuple-typed variable, alias-
   invariant by static type) is one node with a static top-level width.
2. Greedy left-to-right schema matching: unannotated param ← one non-tuple
   node (tuple node vs unannotated param = error demanding annotation);
   `Tuple<k>` param ← one tuple node of top-level width k (preferred) OR k
   consecutive nodes regrouped.
3. One-level SPLICE: a single tuple node of width m against a schema wanting
   m nodes splices once (this is `f((a,b)) == f(a,b)` and `K <@> P`).
   Precedence: direct match to a single `Tuple<m>` param first, then splice,
   then error.
4. Parens disambiguate: greedy is deterministic; the other grouping is
   spelled with explicit parens (`f((t1, a), b)`), possible precisely because
   structure survives.

Gained vs §6b: nested tuple data; Poly structure-preservation restored;
meaningful parens. Lost: only the cross-level recount (`Tuple<4>` over
`((a,b),(c,d))` errors — write the flat spelling). Everything else in §9
survives: width schemas, the VarId surface rewrite, alias fixpoint, hard
arity errors, explicitness, element-level zip tuple kernels, the where+Tuple
refusal, the direct-call checks.

Implementation delta if adopted: spine expansion instead of leaf expansion at
the pack sites; a direct tuple-node binding arm beside the regroup arm (both
seams); Poly back to top-level arity; revise pins tuples/009 (M8 row becomes
error-demanding-annotation), tuples/010 (nested cases), formalism §2.8/§8.2.

STATUS: **ADOPTED — owner ruling, 2026-08-08 ("a good compromise")**. This
supersedes §6b's deep-flatten depth ruling; every other §6b decision
(explicit Tuple<N>, width schemas, no inferred tuple-ness, tuple literals as
arguments, the currying fix) stands. §9 records the §6b implementation; the
revision round updates it in place.

## 7. Recommendation

**Adopt Design A (coexistence, rule R), in the staged order of §8.**
*(Addendum 2026-08-08: Design C in §6b — the owner's flat-normal-form + width-
schema sketch — supersedes rule R3's table if adopted; it is the same
syntax-directed discipline with a cleaner algebra. The A-vs-B decision logic
below is unchanged: C is a refinement of A's side of the ledger, not a third
pole. Awaiting the owner's ruling.)*

Decision table:

| | Coexistence (A) | Explicit tuples everywhere (B) |
|---|---|---|
| Soundness | **Sound** under R3. One undecidable shape (§5.2), resolved by requiring an annotation | Sound by construction (one reading) |
| Already implemented | **The Poly arm ships and passes** (`arity/022,025,027,028`); flattened mode is the whole corpus | Nothing; `#11` blocks line one |
| Fixes M1/M4 (alias asymmetry) | Yes, via R1 | Yes, by construction |
| Fixes M2 (silent operand drop) | Yes, via R3's error rows | Yes (arity of one tuple is fixed) |
| Fixes M3 (docs vs compiler) | Yes — both spellings become legal and mean the same thing | Yes — the flattened spelling stops existing |
| Fixes M6 (tuple-annotated kernel) | Yes, the new R3 arm | Yes, it's the only form |
| Fixes M8 (nested packs) | Only with R4 group tags | Yes, by construction |
| Corpus churn | No *spelling* changes. R2/R3 add errors where today there are miscompiles or silent operand drops, so Step 2 will surface some files — each one a finding, not a chore (it was computing a different program). Unmeasured; a full run after Step 2 is the measurement | **~230+ files**, every one a mechanical rewrite |
| Doc churn | quickstart-1 §5 / §3, quickstart-2, formalism §14.3 need a coexistence paragraph | Docs already correct; compiler moves to them |
| Prereq #11 (`lambda((a,b))`) | Needed for the *tuple* arm only; flattened and Poly arms work without it | **Hard blocker** |
| Distinctiveness | Kept: kernel param shape is meaningful, which is the language's own idiom (`Poly`, `comm`, `where`) | Kept differently: structure lives in the value, not the signature |

**The argument that decided it.** I went looking for a program where no local
rule can choose between pack and tuple, expecting to find one and to have to
recommend B. I found exactly one shape (§5.2: an unannotated single param over a
width-*k* pack), and it is decidable by *requiring* an annotation — the same move
the language already makes for quantities. Everything else in §3 is not an
ambiguity at all: it is the pack decision being taken **syntactically, at three
sites, at three different alias depths** (§2.2), which is a defect under *both*
designs and is fixed by R1 in isolation.

Meanwhile the coexistence rule is not hypothetical: `object_for(comoment) <@>
(data, data)` with a one-param `Poly<T^1>` kernel has been shipping and pinned
since well before this question was asked. Design A generalizes a working
mechanism; Design B discards it and rewrites the corpus for a property (one
concept) that R1 delivers without the rewrite.

Adopt B only if the answer to §5.2 turns out to be unacceptable — i.e. if
requiring an annotation on a single-param kernel over a multi-operand pack is
judged too sharp an edge for the intended audience. That is a taste call, not a
soundness call, and it is the only open question this document leaves.

---

## 8. Migration and test plan

Staged so each step is independently landable and each has a pin. **Steps 1–2 are
required under either design and should land regardless of the eventual choice.**

### Step 1 — make the pack decision structural (R1 + R2)

- Drop the `resolveTypedExpr`-then-`TExprTuple` splat at `TypeCheck.fs:7818`
  (and the same shape at `7743`, `8038`). A variable is one operand.
- New diagnostic for a tuple-typed value in operand position (R2), exempting
  `TypeCheck.fs:7750-7756`'s `(loop, kernel)` pair list.
- Pins (all currently check-OK-then-g++-fail, so each is a real regression test):
  `diagnostics/0xx_tuple_alias_as_pack` (M1, both `P` and `Q`),
  `diagnostics/0xx_tuple_alias_left_of_apply` (M4),
  `diagnostics/0xx_method_for_tuple_operand` (M5),
  `diagnostics/0xx_fusion_result_as_pack` (M7),
  `diagnostics/0xx_nested_pack` (M8).
- Also pin the *positive* side: `K <@> (A, B)` still splats (33 corpus files
  already cover this; add one explicit pin naming the rule).

### Step 2 — the missing arity check (R3's error rows only)

- Replace `TypeCheck.fs:8768`'s `else Ok ()` with a real `ArityMismatch`,
  respecting defaults (`m_req ≤ |rows| ≤ m`) and the `for … in` index-param rule
  (`TypeCheck.fs:11746-11748`).
- Pins: `diagnostics/0xx_kernel_underarity_drops_operand` (§3.4's 12-cell
  program — pin the *count*, it is what the bug got wrong),
  `diagnostics/0xx_kernel_overarity`, `diagnostics/0xx_section_over_3_pack`.
- Expect corpus fallout here and treat it as findings: any file that starts
  failing was silently computing a different program.

### Step 3 — the ambiguity error (§5.2)

- `m = 1`, unannotated, `|rows| > 1` → new code, message naming both escapes
  ("write `k` parameters", "annotate the parameter with a tuple or `Poly` type").
- Pin: `diagnostics/0xx_one_param_kernel_over_zip` — §3.5's exact program, which
  today returns `[2, 4, 6]`.

### Step 4 — `#11`, tuple lambda params

- `LambdaParam.Name : Ident` (`Ast.fs:504`) → pattern-capable. Sweep every reader
  of `LambdaParam.Name` (`TypeCheck`, `Lowering`, `Deduce`, `Ide`, the three ML
  cert walkers, `PplElaborate`, `MathDecls`).
- Pin: `loops/0xx_tuple_lambda_param` — `lambda((a, b)) -> a + b` over
  `zip(A, B)` equals `lambda(a, b) -> a + b` over the same loop, by value.

### Step 5 — R3's tuple arm

- Fixed-arity `TyTuple` param → eta-expand to `|rows|` and pass one `ExprTuple`.
- Pins: `loops/0xx_tuple_param_kernel_zip` (§3.6's `addp`, currently a g++
  failure), and the docs' own pair from `quickstart-1.md:207` as a **duality
  pin**: `method_for(zip(A,B)) <@> lambda((a,b)) -> a+b` ≡
  `method_for(zip(A,B)) <@> lambda(a,b) -> a+b`.

### Step 6 — R4, zip group tags, and lifting the BL3999 guard

- Preserve `isCoIterGroup` (`TypeCheck.fs:7846-7859`) into `buildApplyInfo`;
  shared records computed per group.
- Pins: the compose–apply duality already named in
  `plan-array-expression-fixes.md` Theme A —
  `object_for(f) <@> (A, zip(B,C))` ≡ `method_for(A, zip(B,C)) <@> f`, pinning
  the **cell count** as well as the values. Delete
  `diagnostics/050_zip_in_object_for_pack` and `051_zip_in_method_for_pack` in
  the same change (per the WARN-pin harness rule: a diagnostic removed needs its
  pins removed with it).
- `zip(zip(A,B), C)` joins the guard until this lands.

### Step 7 — docs

- `quickstart-1.md:207` (§5) and `quickstart-2.md:256`: state the coexistence
  rule explicitly — the loop former decides *iteration* (`zip` vs `,`), the
  kernel's parameter shape decides *packing* (k params = flattened, one
  tuple/`Poly` param = whole pack), and an unannotated single param over a
  multi-operand pack is an error.
- `quickstart-1.md:102` (`let e, d, f = myTuple`): either land #10 or change the
  example to the parenthesized form that works.
- `formalism.md:131` ("kernel receives ONE tuple argument") becomes "*may*
  receive one tuple argument, if its parameter is so declared"; `formalism.md:787`
  ("nested tuples preserve structure") becomes true only after Step 6.
- Closes D2 and D3 in `plan-array-expression-fixes.md`.

### Not in scope, but adjacent

The currying/argument-checking hole (§3.9) — `f("hello", "world")` against
`Float` params passing `blade check` — is a separate check-time soundness gap
that keeps getting mistaken for a tuple bug. It deserves its own item.
**LANDED 2026-08-08**, separately (`diagnostics/052`–`055`, `functions/056`).

---

## 9. Implementation status, 2026-08-08

Landed in three stages the same day. Measured against the whole corpus by
`blade check` over all 1398 `.blade` files, before and after: **one** file
changed outcome, and it was the delete-me pin that asked to be changed.

| Stage | What | Status |
|---|---|---|
| Surface | `Tuple<N>` type (`Ast.TyTupleWidth`), `let t = b, c`, `t[k]` | landed — `tuples/001`–`007` |
| §3.9 | direct-call argument type/rank checking | landed — `diagnostics/052`–`055` |
| §6b | the width-schema matcher | landed, then revised to §6c |
| §6c | one-level structural matching | landed — this section |

**What the matcher is, concretely.** Three pieces:

1. **One pack site, spine-based** (R1 + §6c rules 1/3). `packSpine` applies the
   ONE-LEVEL SPLICE — a pack that is a single tuple node opens into its
   components — chasing alias bindings to a FIXPOINT (`resolveTypedExprDeep`)
   instead of the one hop that caused M1/M4. It does not recurse: `(A, (B, C))`
   is two nodes and the second stays a tuple. It is called at
   `inferMethodFor`'s general arm (the former side — which the implicit
   `P <@> k` former also routes through, so M4 needs no site of its own),
   `inferApply`'s `object_for` arm, and the `>>@` compose arm. The
   `object_for(<combinator>)` fold arm is deliberately EXEMPT and says so:
   §3.10's `(loop, kernel)` pair list needs its nesting.
1b. **The spine matcher**, at the top of `buildApplyInfo` — which is where it
   has to be, since `method_for(...)` is built before the kernel is known.
   Greedy left to right: an unannotated param takes one plain node (facing a
   tuple node it demands an annotation); a `Tuple<k>` param prefers one k-wide
   tuple node and otherwise regroups k plain nodes. A directly bound tuple node
   opens into its k components for the loop — a tuple of arrays has nothing to
   iterate as a unit — so direct-bind and regroup produce the *same* loop and
   differ only in which spellings are legal. Runs only when some node is
   tuple-typed, so every pack without one is byte-identical to before.
2. **`Tuple<k>` params, by surface rewrite.** The param is replaced at the
   apply seam by k row params plus a body-entry `let p = (__tp_0, …)`, reusing
   p's VarId — the same mechanism the defaults fill already used. After it the
   schema is all-width-1, so ranks, deduction, grouping, unification, lowering
   and emission are the flattened path unchanged, and `p[i]` is the existing
   `std::get<i>`. Widths come from a `TypeEnv.DeclaredTupleWidths` side channel
   keyed by binder id, populated from the WRITTEN annotation at `inferLambda` —
   never from the resolved type, since an unannotated param unifies INTO a
   tuple as soon as the pack binds it (§5.1's cliff, restated).
3. **The hard errors.** `TypeCheck.fs`'s `else Ok () // handled elsewhere` is
   now `KernelPackArity` (BL3002), naming operands vs width and steering to
   `Tuple<N>`. §5.2's shape gets its own sentence naming both escapes, and the
   spine matcher adds three more: annotation-demanded (`diagnostics/061`),
   cross-level recount (`062`), tuple node to a top-level-arity kernel (`063`).

**Fixed, each pinned:** M1, M2 (both directions), M3, M4, M5, M6 (both the
`Tuple<k>` and the written `(T1,T2)` spelling, both orientations, lambda and
named function). M7 (a fusion tuple in operand position) is no longer a g++
failure — it is a clean BL3002. M8 (nested packs) is now a *diagnostic* rather
than an equivalence: under §6c `(A, (B, C))` is two nodes, and either the
schema annotates the group (`tuples/010`) or it is refused (`diagnostics/061`).

**What §6c changed, versus the §6b implementation this section first
recorded** — all measured, not predicted:

| Program | §6b | §6c |
|---|---|---|
| `lambda(a,b,c) <@> (A,(B,C))` | ran, ≡ flat | BL3002, demands `Tuple<2>` |
| `lambda(x, y: Tuple<2>) <@> (A,(B,C))` | ran | ran, same values |
| `lambda(r: Tuple<4>) <@> ((A,B),(C,D))` | ran | BL3002, cross-level recount |
| `lambda(p: Tuple<2>, q: Tuple<2>) <@> ((A,B),(C,D))` | ran (by recount) | ran (by structure) |
| `object_for(poly) <@> (A,(B,C))` | arity **3** | arity **2**, then BL3002 |
| `outer(((1,2),(3,4)))`, nested tuple data | inexpressible | runs |
| `f((t1, a), b)` — the other grouping | inexpressible | runs |

**Not done, deliberately:**

- **Step 6 / R4, zip group tags.** `(A, zip(B,C))` still hits the BL3999
  guard, and `diagnostics/050`/`051` still pin it. Independent of the matcher.
- **Step 4 / #11, `lambda((a, b))` patterns.** The `Tuple<N>` annotation
  reaches the same programs via projection, which is why §6b called #11 wanted
  but not blocking.
- **A `where` clause together with a `Tuple<N>` param** is REFUSED
  (`diagnostics/060`) rather than remapped: `comm`/`anticomm` address params by
  position and the parallel strategies by name, and the expansion moves both.
  Both failure modes are silent, and `comm` between two pairs has no settled
  meaning to remap to.
- **A `Tuple<N>` whose element types are not scalars** still fails to
  monomorphize (`tuples/002`'s note, now corrected there). The earlier
  prediction that the §3.9 fix would close this is MEASURED FALSE: neither the
  direct-call checks nor the matcher unifies anything into the annotation's
  element slots, so they default to `double`. That needs per-call-site
  instantiation of the callee's arrow — its own item. §6c widens the blast
  radius slightly: it makes NESTED tuples expressible, and a nested projection
  chain `r[0][1]` needs the element slots filled. The chain itself is fine —
  parse, typing and lowering all handle it, pinned in `tuples/012` with the
  fully written type `((Float64, Float64), (Float64, Float64))`. Only the
  width-only spelling cannot carry the inner type.
- **Expanding a tuple ARGUMENT into k scalar params** at a direct call. `f(t)`
  on a 2-param `f` already means partial application; the two readings cannot
  both exist. Regrouping is one-directional and only fires where the 1:1
  pairing has no reading at all.
- **§6c rule 3's one-level splice is unreachable at a DIRECT CALL**, and this
  is a deliberate deviation rather than an omission. `f((a, b))` on a 2-param
  `f`, and `two(((a,b),(c,d)))` on a two-`Tuple<2>`-param `two`, are both
  UNDER-application, which `ExprApp` already eta-expands into partial
  application before the seam is reached — they already mean something. The
  one-directionality principle (never redirect a call that type-checks) wins.
  The splice is implemented and live at the OPERAND seam, which is where
  `f((a,b)) == f(a,b)` and `K <@> P` are actually needed, and the splice code
  at the call seam stays as the fallback for lists that have no other reading.
