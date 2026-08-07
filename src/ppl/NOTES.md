# ppl/NOTES.md — the typed-Dist arc

Internal compiler notes on `Dist<r, T like axes>`: how it is parsed, how the
type checker treats it as a genuine nominal type, the order guard that keeps
projections honest, and where it disappears before codegen ever sees it.
This is the file referenced (by that exact relative path, `ppl/NOTES.md`,
read as relative to `src/`) from doc comments at `src/Ast.fs:159`,
`src/IR.fs:772`, `src/Parser.fs:664`, `src/ProviderStatics.fs:36`,
`src/Types.fs:511`, `src/TypeCheck.fs:3041`, and
`src/providers/ZarrSimplexBlocksPlan.md:188`. For the user-facing surface —
the 23 formers, import gating, independence licensing — see
`docs/features/ppl.md`; for the reference-prototype/oracle architecture see
`src/ppl/README.md`. This file is scoped narrowly to the typed-Dist arc
itself.

## 1. Surface syntax — `TyDist`

`Dist<order, Elem like I1, ..., Ik>` is parsed at `src/Parser.fs:655-679` as
`TyDist (orderExpr, elemType, axes)` (AST node at `src/Ast.fs:164`). `order`
leads the angle-bracket list specifically because it is an *expression* (any
statically-evaluable int — literal, `let static`, or static-function call,
the same replicate-count contract used elsewhere in the language), which
keeps the grammar unambiguous before the rest of the list, which reuses
`Array`'s `Elem like I1, ..., Ik` index-list syntax verbatim. A bare `Dist`
token with no `<` falls through to the ordinary `TyNamed` case, so `Dist` is
not a reserved word outside this one construction.

## 2. Lowering — the `-1` sentinel

`lowerTypeExpr`'s `TyDist` arm (`src/TypeCheck.fs:418-433`) resolves `order`
through the same two-tier static evaluation as everywhere else (cheap
`evalConstExpr` first, then full `StaticEval` against `checkModule`'s
pre-pass `StaticValues`/`StaticFunctions` maps — see `staticEnvOf`,
`TypeCheck.fs:124-134`). `lowerTypeExpr` itself has no error channel, so an
order that fails to resolve to a static int `>= 1` lowers to the sentinel
`IRTDist (-1, elem, axes)` instead of failing immediately. The sentinel is
caught downstream, at the annotation-*consumption* sites
(`inferLetBindingValue` / `checkFunctionDecl`), by
`irTypeHasBadDistOrder` (`TypeCheck.fs:8283-8293`, walking through
`ArrayElem`/`IRTTuple`/`FuncElem` wrappers to find a buried `-1`) — reported
as `DistOrderCompileTime`. This is the same two-step "sentinel now, error at
the consumption site" pattern used for the ragged-no-prior check elsewhere
in the checker; it is not special to `Dist`.

## 3. Checker-level type — `IRTDist`, strict and nominal

`IRTDist (order: int, elem: IRTypeG<'Ext>, axes: IRIndexTypeG<'Ext> list)` —
`src/Types.fs:510-518`. `order` here is a plain **static** int, not
`IRTNat`; unification does not treat it as a value to be solved, only as a
tag that must match exactly. Unification (`src/Unify.fs:716-730`) is
**strict** — the same regime as `IRTIdxTagged` (nominal index tags): no
asymmetric/covariant arms. Two `IRTDist` values unify only if the carried
orders are equal, the axis lists have the same length, and each axis pair
is index-compatible; `elem` unifies recursively. A bare tuple of arrays
never flows into a `Dist`-typed position — only the `__dist_pack(...)`
construction intrinsic (`inferDistPack`, `TypeCheck.fs:4056-4072`, detailed
below) and the Dist-typed operators (`inferDistBinOp`,
`TypeCheck.fs:5812-5856`) ever *produce* one.

This nominal strictness is what makes `Dist` usable as an ordinary function
parameter/return type: `function combine(a: Dist<2,...>, b: Dist<2,...>)
-> Dist<2,...>` type-checks exactly like any other typed signature, and the
order is part of the type identity, not a runtime-checked value — mixing a
`Dist<2,...>` and a `Dist<3,...>` at a call site is a type error
(`DistOrderDisagree` on `+`/`-`; a straight unification failure elsewhere),
never a runtime panic.

The construction intrinsic, `__dist_pack(kappa1, ..., kappar)`
(`inferDistPack`, `TypeCheck.fs:4056-4072`, emitted by the `ppl` elaborator
after it builds the fused cumulant tower — never written by hand), makes the
"only the TYPE is nominal" point explicit in its own doc comment: the typed
node it produces is a **plain `TExprTuple`** — already exactly the
representation the type erases to at zonk (§5) — wrapped in the nominal
`IRTDist` type. `order` is fixed by the argument count, `elem`/`axes` by
`kappa_1`'s array type. No new lowering or codegen path exists for
constructing a `Dist`; only the type-level bookkeeping is new.

## 4. The order guard — `cumulant(d, k)`, `BL3007`

`cumulant(d, k)` (surface: `alias.cumulant(d, k)`, normalized by the `ppl`
elaborator's `stripQualified` to the internal marker `__ppl_cumulant(d,
k)`, PplElaborate.fs ~2278) is deliberately **not** a former —
`PplElaborate.fs:71-72` marks it explicitly: *"'cumulant' is NOT a former
name: it is a checker-level projection on Dist-typed values
(TypeCheck.inferCumulantProj), so elaboration lets it flow through
untouched."* It is resolved by `TypeCheck.inferCumulantProj`
(`TypeCheck.fs:4080-4096`, dispatched from the `__ppl_cumulant` marker at
`TypeCheck.fs:3048-3049`):

1. Infer `dExpr`'s type and resolve it through the current substitution.
2. If it is not `IRTDist (order, elem, axes)`, refuse with `CumulantNeedsDist`.
3. Evaluate `kExpr` as a compile-time int (same static-eval contract as
   everywhere else); a non-static `k` refuses outright (no error variant —
   a plain `Other` message, since this path has no dedicated diagnostic
   type for it).
4. `k < 1` → `CumulantOrderPositive`. `k > order` → `CumulantOrderExceeds`
   — *"insufficient stochastic order. Construct with a higher order
   (`dist(A, k)`) or project a carried component."*
5. Otherwise the result type is `distComponentType k elem axes`
   (`src/IR.fs:778-...`: `kappa_1` is the plain array over `axes`; `kappa_k`
   for `k >= 2` is the packed `SymIdx<k, fusedExtent>` joint-cumulant
   tensor), and the expression becomes `TExprTupleIndex (tD, k - 1)` — a
   plain tuple-index read at position `k - 1`, because that is exactly what
   the value already is underneath (see §5).

Both `TypeError` variants funnel to code `BL3007`
(`src/TypeEnv.fs:631-632`, the shared "invalid builtin argument" bucket —
alongside every other builtin-argument-shape rejection in the checker, not
a `Dist`-specific code). The key property this guard buys, stated in the
doc comment right above it (`TypeCheck.fs:3043-3044`) and pinned by
`tests/corpus/ppl/019_dist_order_guard.blade`: it "works in any expression
position on any Dist-typed value — including function parameters, which
the elaboration-level registry could never see." The elaboration-level
`DistInfo` registry (used by the module-level formers described in
`docs/features/ppl.md`) only knows about `dist` bindings visible to
source-to-source rewriting; a `Dist`-typed function *parameter* has no such
registry entry; `inferCumulantProj` is the only place that guard can live,
because it runs during real type inference, after parameters are bound.

Contrast: the *elaboration-time* order guards on `dist_jet`/`dist_map`
(strict-vs-closed budget) and `dist_reweight` (order-spending) are a
different mechanism entirely — they check the flat `DistInfo.Order` field
in `PplElaborate.fs`'s registry and fail with the generic elaboration code
`BL5100`, not `BL3007`. Both guards enforce the same underlying invariant
(never read a cumulant order that was not carried), but one is a checker
diagnostic on a nominal type and the other is a source-to-source rewrite
failure — see `docs/features/ppl.md` §4 for the user-facing framing of that
split.

## 5. Erasure — `Zonk.fs:120-128`

`Dist` is a **typecheck-time-only** invariant. `Zonk.zonkType`'s `IRTDist`
arm is the single erasure point:

```fsharp
| IRTDist (order, elem, axes) ->
    // ERASURE POINT: Dist<r, T> is a typecheck-time invariant. All
    // Dist-aware checking (order guard, operator dispatch, signature
    // unification) happens during inference, before zonking; downstream
    // of the checker a Dist value IS the tuple of its packed cumulant
    // component arrays, so Lowering/IR/CodeGen never see IRTDist (the
    // CodeGen sentinel arm is the backstop if one leaks).
    let e = zonkType subst elem
    IRTTuple (distComponentTypes order e axes)
```

Every `IRTDist` in a zonked type becomes `IRTTuple [distComponentType 1 e
axes; distComponentType 2 e axes; ...; distComponentType order e axes]` —
the ordinary tuple-of-arrays shape that `TExprTupleIndex` (§4 step 5)
already assumed a `Dist` value *was*, all along, underneath the nominal
typing discipline. Because every Dist-aware judgment (the §4 order guard,
`inferDistBinOp`'s operator dispatch, the independence-license discharge at
call sites, strict `IRTDist` unification) runs strictly during inference —
*before* zonking ever touches the tree — there is nothing left for zonking
to check; it is a pure structural rewrite.

The consequence: **nothing below the type checker ever sees a `Dist`.**
Lowering, the IR validator, and CodeGen all operate on the zonked tree, so
by the time any of them run, a former `Dist<r,T>` value is indistinguishable
from a hand-written tuple of `r` packed `SymIdx` arrays — no new IR node, no
new runtime representation, no codegen special-casing. `CodeGen.fs:957-963`
carries a defensive sentinel arm on `IRTDist` purely as a backstop: reaching
it means Dist erasure was skipped somewhere upstream (a compiler bug in
this arc, never a user-facing situation). It records a diagnostic —
*"irTypeToCpp: IRTDist reached codegen — Dist erasure was skipped at
lowering"* — to the expression warnings cell and emits the placeholder type
name `BLADE_ERROR_DIST_TYPE` rather than silently rendering a
wrong-but-plausible C++ type.

## 6. The full arc, end to end

```
Dist<r, T like axes>              surface syntax           Parser.fs (TyDist)
        |
        v
IRTDist (n, elem, axes)           lowered type,             TypeCheck.fs
  (n = -1 sentinel if order         -1 sentinel for            (lowerTypeExpr,
   isn't a static int >= 1)         non-static/invalid          irTypeHasBadDistOrder)
        |
        v
IRTDist, strict nominal type      checker-level type,       Types.fs, Unify.fs
  unify: order/axes must match      order guard on
  exactly; cumulant(d,k) order-     cumulant(d,k) is
  guarded (BL3007) at any           TypeCheck.inferCumulantProj
  expression position
        |
        v
IRTTuple [kappa_1 .. kappa_r]      ERASURE (zonking)         Zonk.fs:120-128
        |
        v
(nothing — it's just a tuple      Lowering / IR / CodeGen
 of packed SymIdx arrays now)       never see IRTDist
```
