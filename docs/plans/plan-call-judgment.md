# One call judgment

Status: BUILT 2026-09-26 on fix/drf-call-judgment (direct + qualified calls, ascription extents, match merge, P2-44/45/46 fold-ins); see §5 for what is left.

## 1. The problem

Argument/parameter agreement and value/ascription agreement are enforced by
separate seams that do not share code, and each seam grew its own subset of
the rules. A deep review of master @81cdd61b confirmed four families of hole:

| id | seam | defect |
|----|------|--------|
| P0-4 | direct application (`dispatchAppOrIndex`, FuncElem arm) | arguments are never unified with parameters; only hand-added checks run (irreps, per-slot rank, units, slot-count rank, mut, element CLASS, extent, co-iteration). A dense 3x3 into a `SymIdx<2,3>` param is read as packed storage; `Nat<Lon>` into `Nat<Lat>` reads out of bounds; `Float` into `Int64`, struct `Q` into `P`, a tuple into `Float64`, `Array<Int64>` into `Array<Float64>` all pass `check` and die in g++. |
| P0-5 | module-qualified application (`M.f(args)`, TypeCheckInfer ExprApp arm) | builds `TExprApp` by hand and repeats only two of the direct seam's checks: no units, no mut, no extent, no co-iteration, no arity; a qualified ARRAY read `Geo.w(1)` is typed as a fresh variable. |
| P1-18 | `let` / return / `if` / `match` ascription | static extents never compared: `let x: Array<F like Idx<5>> = a3` copies 5 out of a 3-buffer. |
| P2-50 | `checkMatch` | swallows `checkExpr`'s specific error and retries with a lenient unify; duplicates `inferMatch`. |

### 1.1 The "keeps HM alive" rationale, re-examined

The comments at the direct seam say unifying would over-constrain: a
`function`'s type is bound ONCE (`bindVarSimple`, no scheme) so its type
variables are shared by every call site. That part is TRUE (the second
reviewer's claim that `ExprVar` instantiates the callee's scheme holds only
for let-generalized values; `function` declarations never get a scheme).
The conclusion drawn from it is not: the fix for shared variables is to
INSTANTIATE at the call, which is exactly what HM application does. The
judgment below instantiates the callee's signature per call (fresh copies of
every open variable in it) and unifies the COPY against the arguments. The
declaration's own variables are never bound by a call, so the IR-phase
monomorphizer (`IRMono`), which reads call-site argument types and the
declaration's still-open polymorphic vars, sees exactly what it sees today.

## 2. The judgment

`judgeCall env callee paramTys retTy tArgs` (TypeCheckSupport.fs), called by
every application seam. Steps, in order; the first failure is the verdict.

1. **Width schema** — `regroupArgsByWidth` (unchanged).
2. **Stand-downs** (unchanged, now in ONE place): a variadic `Poly<T^r>`
   parameter (monomorphization owns the call) and UNDER-application (the
   arity error must not be buried) skip steps 4–5. Over-application judges the
   prefix the arrow consumes; the rest re-dispatches on the result.
3. **Binding-form checks** — `mutClash` (BL4005) plus NEW mut ALIASING
   (P2-46): the same root binding passed to a `mut` parameter and to any other
   parameter of the same call is refused (BL4005, "aliases argument k").
4. **Instantiate → unify** — for a DECLARED function (callee `TExprVar` whose
   binder id has a `FuncSigVarRange`), copy exactly the open variables its
   declaration minted: `checkFunctionDecl` brackets them with `Subst.NextId`
   (ids are monotonic), which is what HM would quantify. A variable shared
   with the environment is never copied. A lambda, a function-typed parameter
   or a curried head quantifies NOTHING: its open variables belong to its
   environment, and binding them would pin a lambda to its first call's
   types while its emitted body keeps the zonk default (fable review #1).
   Arity pins, rank lower bounds and literal defaults travel with a copy; the
   polymorphic mark does not (a copy is never a declaration variable). Then
   per position, `argPairClash copy(param) arg`, a structural walk that binds
   ONLY copies and uses unify only at a copy variable or a closed fallback,
   under the coercions the emitted C++ call performs and the old seam
   allowed:
   - **units stripped DEEP** (array elements, tuples, Dist elements) — step 5
     owns units; unify's unit arm would otherwise bind a generic `T` to
     `Float<m>` and give `variance` an `m` return instead of `m^2`;
   - **scalar widening** at the top level and in tuple components (a concrete
     scalar into a concrete scalar parameter it promotes to, never narrowing,
     never inside an array or function type); a numeric LITERAL adapts to any
     numeric parameter of its kind (`ident32(21)`);
   - **tags**: untagged into `Nat<I>` and tagged into plain `Int64` both
     stay legal (the cast/bounds story is the index seam's); a tag vs a
     DIFFERENT tag refuses; `Nat<_>` (IRefAny) is a wildcard;
   - array **virtual/stored** character is not compared (a range passed to an
     array parameter is materialized);
   - a caret claims RANK while unify's arity pin counts SLOTS, so a packed
     `SymIdx<2,n>` array meeting a `T^2` copy is accepted by rank without
     binding (review #4d);
   - two `Dist`s keep the class-only judgment (order/axes are the ppl
     formers'); loop objects and `Poly` packs stand down.
   An argument OPEN at a position is not judged there (it keeps today's
   rank-lower-bound propagation and is judged by the post-zonk sweep), so no
   caller variable is bound by the judgment (review #2).
   Diagnostics, most specific first, at the argument's span: block-spec /
   irreps identity, per-slot component rank (`IndexRankMismatch`), slot-count
   rank (`ArgRankMismatch`), abstract-variable conflict (same signature
   variable taught two types; wording unchanged — it runs on the declaration's
   still-open variables, which calls never bind), then everything else as
   `ArgTypeMismatch` (BL3001).
5. **Units** — `unitClash` (unchanged: BL3006 / BL3010 / magnitude).
6. **Static extents** — `staticExtentClash` (NEW shared predicate, §3).
7. **Co-iteration** — `coIterClash` (unchanged, BL3016).
8. **Result** — the instantiated return when it is fully determined by the
   arguments (no copy variable left open), else the declared return (today's
   behaviour). `unitStampedReturnOnto` decides "deduced" on the DECLARED
   return and stamps the recorded unit transform onto the chosen result
   (review #3 — otherwise a concrete instantiated return would silently turn
   the stamp off).

`firstArgTypeClash` and `firstAbstractVarConflict`'s eager call are subsumed
by step 4; `firstAbstractVarConflict` stays for the post-zonk sweep
(`collectAppRankErrors`), which judges arguments that were open at the seam.

## 3. Shared static-extent refinement

Extents are deliberately NOT part of type identity (memory
extent-identity-design: shape monomorphization at codegen, ragged future), so
this is a post-unify REFINEMENT, never unification.
`staticExtentClash subst expected actual` compares literal-vs-literal extents
(via `tryEvalIntIR`) slot by slot for equal slot counts, one level into
tuples. Symbolic / ragged / runtime extents stand down. Called from:

- the direct/qualified call judgment (step 6, BL3016 `ExtentArgMismatch`);
- `checkExpr`'s default arm after a successful unify — this one site covers
  let ascription, annotated function returns, block finals and match arms
  checked against an expected type. Reported as a new TypeError case
  `ExtentAscribeMismatch` mapped to the EXISTING code BL3016 (no new BL code,
  so no surface/diagnostics.json churn);
- `if` branches and inferred `match` arms: two branches with different
  literal extents are refused (the node's type is one branch's, so the other
  would be read at the wrong length).

The existing `providerReadExtentClash` keeps its provider-specific wording and
runs first at the provider-read forms.

Coverage limit: a GENERIC callee's result carries the callee's synthesized
symbolic extents (`requireArrayArgMinRank`'s `IRParam` records), so
`let r: Array<F like Idx<3>> = mean_rows(A)` stands down here -- the
literal-vs-literal rule has nothing to compare.

## 4. Seams and who calls what

| seam | today | after |
|------|-------|-------|
| direct app (`dispatchAppOrIndex` FuncElem) | hand checks | `judgeCall` |
| qualified app `M.f(..)` | own node + 2 checks | rewritten to the unqualified path (`ExprVar "M.f"`), so defaults, eta-expansion, arity lift, constraint discharge, dispatch and `judgeCall` are the SAME code; side tables (mut positions, co-iteration obligations, unit transforms, where-constraints) are snapshotted per module and re-registered under `alias.name` at import, like `Defaults` |
| qualified array read `Geo.w(i)` | fresh-var result | ArrayElem arm of `dispatchAppOrIndex` (tag checks, residual views) |
| kernel app (`buildApplyInfo`) | real unify, per element | unchanged: its pairing is element-of-iteration vs parameter and it already binds (the kernel is a lambda or an eta wrapper); listed as a follow-up to share `staticExtentClash` for baked kernel params |
| let / return / block / match-arm ascription | `checkExpr` default arm unify | + `staticExtentClash` |
| `if` / inferred `match` | branch unify | + branch extent agreement |
| `checkMatch` | swallow + lenient retry | one `matchWith` function parameterized by the arm judgment; `checkExpr`'s error propagates |
| impl method body | `let _ = unify` | checked result (P2-44) |
| composition `f >> g` | `let _ = unify` | checked result (P2-44) |
| static pre-registration | `let _ = unify` | left: forward-ref placeholder unification whose failure is reported by the real check |
| `from M import x` | silently ignored when missing | refused (P2-45) |

## 5. Left out, with reasons

- Units through generic helpers (`funcUnitTransform` gaps) and array-literal
  element unification: separate rounds, not in this path.
- Binding CALLER variables from callee signatures (arguments open at the call
  head): an inference change with its own blast radius; the post-zonk sweep
  keeps catching rank.
- Kernel application rerouting (see table).
- The KERNEL-position name-keyed tables (`FuncCommGroups`,
  `FuncAntisymGroups`, `FuncParallel`, `FuncFoldBuiltin`, `FuncDeducedPairs`,
  `PackDeducedComm`, `MutualReturnFuncs`, `JoinLegLists`) are not snapshotted
  per module: a qualified function in `<@>` position (`method_for(A) <@>
  M.f`) is already refused by the kernel slot, so none of them is reached
  through `alias.name` today. Only the call judgment's tables ride
  `TypeModuleExport.Callees`.
- Assignment statements (`x = e`, TypeCheckInfer block/impl statement arms)
  still discard their unify result -- not a call seam; follow-up.
- Full let-generalization of `function` declarations (a real scheme at
  `bindVarSimple`): would change what kernel application and ascriptions bind
  and push work onto IRMono; instantiate-at-the-judgment gets the soundness
  without it.

## 6. Migration order

1. `judgeCall` step 4 + shared extent predicate at the direct seam; update
   the stale rationale comments. Run types/index-types/functions/units/arity/
   symmetry/diagnostics.
2. Qualified calls through the unqualified path; side-table snapshots;
   delete the duplicated blocks. Run modules/multifile + stdlib consumers
   (plot/display, rand, math, ml, ppl).
3. Ascription extents in `checkExpr` + `if`/match branch agreement.
4. `matchWith` merge (P2-50).
5. Fold-ins: mut aliasing, P2-44 discards, P2-45 import.
6. Corpus regression tests; examples/ and examples/physics/ check sweep;
   full `blade test --interp`.

## 7. Adversarial review (fable, before implementation) and what changed

Verdict: SOUND-WITH-CHANGES. §1.1 verified (functions bind with no scheme;
`ExprVar` instantiates only let-generalized values). Adopted:

1. Instantiate only a DECLARED function's own variables (`FuncSigVarRange`
   watermark), never a lambda's / parameter's / environment-shared ones.
2. The judgment binds only copies; an open argument is not judged, and the
   fallback unify runs only on closed arguments.
3. `unitStampedReturnOnto`: "deduced" is decided on the declared return, the
   stamp lands on the instantiated one; units are stripped DEEP.
4. Coercions added after the census: tagged into plain `Int`, tuple-component
   widening, virtual/stored not compared, packed `SymIdx` into `T^k` by rank,
   Dists class-only; integer to integer in either width (a recurrence index
   into an `i: Int` parameter is an established idiom -- 11 ppl/ml/ad corpus
   programs).
5. Qualified callees: the `lookupUnitTransform` tail fallback is removed (the
   `alias.name` entry is registered), so two modules' same-named functions no
   longer share a transform.
6. Found during implementation: the partial-application / placeholder eta
   arms pinned their lambda to the callee's DECLARED types; with an
   instantiated call result inside the lambda that pin bound the declaration
   itself, so both now pin against an instantiated copy. A recursive call's
   result is not instantiated (the declaration is still being inferred).
7. A copy an earlier argument already taught defers to
   `firstAbstractVarConflict` (the monomorph's compatibility rule), so the two
   judgments of one shared variable cannot disagree; tree-tag mismatches defer
   to the post-zonk tree sweep's more precise BL4003.

## 8. Census (blade check over tests/corpus + examples + examples/physics)

Against master @81cdd61b, 2215 files: examples/ and examples/physics/ are
unchanged; three corpus pins changed verdict, each the old laxity --
functions/128 and /131 (curried extent mismatch, now BL3016 at compile time
instead of the BL8011 runtime abort) and func-arrays/012 (computed-row extent
vs the annotation, now BL3016 instead of the runtime row guard).
