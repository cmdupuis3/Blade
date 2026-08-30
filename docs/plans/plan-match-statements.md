# Match statements: census, the `while` guard, and matching on types

Status: AUTHORED 2026-08-30 from a two-agent probe audit. §2's bug cluster and
§4's phase 1 LANDED the same day (feat/match-arc); §4's phase 2 (abstract
rank peeling) and §3's P0 (the `while` guard itself: parse/typecheck/
IRBreakIf/freeze/BL8010 abort, C++ + interp lanes, llvm refuse, AD refusal,
unroll decline) both LANDED 2026-08-30; §2's open item 1 (the recursive
match emitter) LANDED 2026-08-30. Open: §3's P1 (`converged_at`) and P2
(window riding), §4 phases 3-5, and §2's remaining ledger.

Sources: docs/formalism.md §7.5 (rec arrays), plan-fortran-killer.md §2 (the
while-guard sketch this refines). Claims marked VERIFIED were probe-confirmed.

## 1. What match is today

**Two grammars share zero code.** Ordinary `match` (`ParserGrammar.fs:1050-1076`;
guard keyword is `if`, via a restricted `parseGuardExpr` — no `<@>`, no `|>`, no
lambdas) and the recursive-array match (`parseRecArrayBinding`,
`ParserGrammar.fs:1434-1532`), a hand-rolled grammar reached only from `let rec`
that produces `ExprRecArray`, never `ExprMatch`, and constructs no `Pattern` at
all. Any match-statement feature must decide which grammar it belongs to; the
`while` guard (§3) belongs to the second, type patterns (§4) to the first.

**Pattern kinds.** Parser-reachable: wildcard, var, literal (Int/Bool/String
only — no float patterns, though three downstream evaluators carry dead
float-literal arms), tuple, cons, struct (`Name { f, g: p }`), variant
(`Name(...)`). Dead AST cases: `PatGuarded` (guards ride `MatchCase.Guard`),
`PatTyped` (no ascription syntax in patterns — §4's cheapest entry point: the
typecheck arm at `TypeCheckSupport.fs:896-899` and every downstream walker
already handle it; only the parser production is missing).

**Four evaluators, four pattern sets** — the structural debt:

| | handles | non-exhaustive |
|---|---|---|
| C++ `renderMatchExpr` (`CodeGenExpr.fs:801+`, nested ternaries + IIFEs) | wild/var/lit/tuple(1 level)/variant; **cons falls through to a dangling identifier; struct collapses to std::get on a real struct; nested tuples bind nothing** | BL8002 |
| Interp (`Interp/Core.fs:993-1042`) | all of the above CORRECTLY, incl. cons and structs | BL8006 |
| LLVM (`EmitLlvm.fs:2481-2538`) | wild/var/int-lit only, refuses loudly (incl. strings) | BL8002 |
| StaticEval (`StaticEval.fs:672-714`) | wild/var/lit(+float)/tuple/struct; no cons/variant | error |

**Semantic checks that do NOT exist:** exhaustiveness (a Bool match with one
arm passes `check`), redundancy/unreachable arms (an irrefutable arm silently
kills everything after it), guard-coverage interaction. A zero-arm match parses
and is caught only at IRValidate as BL6001.

## 2. The bug ledger

### Fixed 2026-08-30 (feat/match-arc), each corpus-pinned

1. **Tuple slot skew — silent wrong answer** (`CodeGenExpr.fs:912-924`): a
   tuple pattern in a NON-last guarded arm re-derived slot indices over the
   filtered binder list, so `(_, b, _) if b > 0 -> b` read slot 0 and
   returned 10 for (10,20,30). VERIFIED, fixed (index rides the binding),
   pinned by basic/053.
2. **Arm result types never unified** (`inferMatch`/`checkMatch`,
   `TypeCheckInfer.fs:10715+`): `let _ = unify ...` discarded the result, so
   an array arm beside a scalar arm shipped to g++ as an ill-typed ternary.
   Now binding; `checkMatch`'s checkExpr-first literal-flex leniency
   (comoment_prod's `| 0 -> 1` into `-> T^1`) is preserved — only the
   fallback unify became strict. basic/054.
3. **Guards never typed** (same functions): an Int64 — or an ARRAY — guard
   sailed through to C truthiness. Now unified with Bool, with a message
   saying a guard is a predicate. basic/055.
4. **The non-exhaustive abort IIFE was hardcoded `-> double`**
   (`CodeGenExpr.fs:806/810`): on any non-double match the ternary chain's
   common type became double and the program died as a g++
   float-conversion error — BL8002 was unreachable except for
   double-valued matches. Now typed from the match result (`return {};`
   value-initializes anything). basic/056 pins the abort actually firing.
5. **Wrong-arm caret**: `List.map + sequenceResults` let later successful
   arms re-stamp the ambient error span (the compact-literal walker fixed
   the same class at `TypeCheckInfer.fs:9358` — its `go`-fold shape copied
   into both match checkers).
6. **Bare payload-variant pattern was an irrefutable binder**
   (`TypeCheckSupport.fs:787-791`): `| Some ->` bound a fresh variable named
   Some, matched everything, and made `| None ->` dead with no diagnostic.
   Now an error demanding `Some(p)` / `Some(_)`. sum-types/008.
7. **`while`/`do` steer**: not keywords (deliberately — programs may bind
   them), so the imperative refugee's first program died as a bare
   `BL2001: Unbound variable: while`. The unbound-variable message now
   carries the declarative-iteration steer — fired where the name is known
   unbound, so a real binding never trips it. diagnostics/078.
8. **Constant-scrutinee match fold hoisted** (§4 phase 1): see below.

### Open, in priority order

1. **`renderMatchExpr` recursive rewrite** — the real work (~1-2 days). One
   recursive test/bind emitter fixes cons-in-match (dangling identifier,
   VERIFIED), struct-in-match (std::get on a struct, VERIFIED), nested tuple
   binding (binds nothing, VERIFIED), and closes three interp divergences at
   once. Until then those shapes are check-clean and g++-broken.
2. **Unreachable-arm warning** (~1 day, new WARN code): an unguarded
   var/wildcard arm followed by anything, and duplicate literals. Cheapest
   real safety win.
3. **Or-patterns** `| 1 | 2 ->` (~0.5 day): PatOr + identical-bindings check
   + expansion to N arms at lowering; zero IR/codegen change. Highest
   value-per-effort of the missing features.
4. **Exhaustiveness for closed types** (variants + Bool, 3-5 days): all the
   data is in `env.VariantTags`; ints/strings stay open-with-catch-all.
   Guarded arms don't count toward coverage (the rule
   `IRMono.foldConstIntMatch` already encodes).
5. **Range patterns** `| 0..9 ->` (~0.5 day): parse-time desugar to a guard;
   worth doing together with exhaustiveness, cosmetic before it.
6. Declined: active patterns (collides with `()` application; mask/compound
   is Blade's predicate-dispatch answer); match on array VALUES (would
   reintroduce shape branching the design exists to eliminate — the
   rec-array snoc stays confined to `let rec`, and TYPE-level dispatch is
   §4's job).

## 3. The `while` guard on recursive arrays (P0 LANDED 2026-08-30; P1/P2 open)

P0 notes (as built): the guard is a bare `TokIdent "while"` in `parseConsArm`
(seed arm rejects, `if` steers to `while`); `RecArrayDef.Guard` walked by the
six elaborators + collectFreeVars + the Grad walkers; `inferRecArray` runs the
guard through the SHARED lag-hoist table with the slice (bounds/zero-history
free), desugars the stop ordinal + freeze epilogue in surface AST, and injects
the TExpr-only nodes (`TExprBreakIf` after the stop record; the BL8010
`TExprConstraintCheck` after the freeze) into the CHECKED block -- guard
unified with Bool post-check with a predicate-steer message.
`IRConstraintCheck` gained a `code` field (BL8001 stays the default) so the
abort renders `error[BL8010]` naming the array and budget in both lanes.
The C++ break peeks (not pops) the live alloc frame's frees before `break`;
the interp twin is an `InterpBreak` exception caught per IRForRange level.
`unrollForRanges` declines IRBreakIf bodies; EmitLlvm refuses loudly;
`expandRecArray` refuses guards (BL5500). Corpus: recursive-arrays/012-016
(Newton freeze, budget abort, seed-arm reject, non-Bool reject, rank-2
freeze) + ad/023; interp lane byte-identical.

Design refined from plan-fortran-killer.md §2. Surface:

```blade
type It = Idx<200>                       // a BUDGET, not a trip count
let rec u: Array<Float like It, Y, X> =
    match u with
    | zero -> zero
    | zero :: s -> zero :: u0
    | prefix :: n while residual(prefix(n - 1)) > tol -> prefix :: sweep(prefix(n - 1))
```

Semantics: defined up to the first n where the guard is false; frozen (last
slice repeats) after; guard still true at n = budget-1 → runtime abort (new
BL8xxx naming the array and budget). Extent stays static — the guard only
stops early, so the §7.5 decidability fence and zero-history rule are
untouched. The hand-written freeze idiom stays compilable (VERIFIED), so
`while` is a cost-and-diagnosis feature, not an expressiveness one.

Seam map (agent-verified):

- **Parse** (~0.5d): `parseConsArm` (`ParserGrammar.fs:1470-1499`) accepts an
  optional bare `TokIdent "while"` + `parseGuardExpr` between the step var
  and `->` — NOT a keyword (the `repro` where-conjunct precedent), so no
  program binding `while` breaks. Reject on the seed arm. `RecArrayDef`
  (`Ast.fs:554`) gains `Guard: Expr option`; ~15 one-line walks in the six
  domain elaborators + `collectFreeVars`'s ExprRecArray arm
  (`TypeCheckSupport.fs:184` — miss it and guard captures go unreported).
  Once landed, the `| prefix :: n if ...` error should steer to `while`.
- **Typecheck** (~1d): all of `let rec` funnels through `inferRecArray`
  (`TypeCheckInfer.fs:10160-10380`). Guard unifies with Bool (do NOT rely on
  the ordinary-match path — different grammar). The two hard sub-problems
  are ALREADY SOLVED there: `guardsFor` (`:10263`) classifies exactly the
  legal read shapes (n, n-c, constants), and `rewritePrefixReads` (`:10304`)
  applies the zero-history clamps — run the guard through both alongside
  the slice.
- **IR + three emitters** (~3d, the actual cost): add ONE statement node,
  `IRBreakIf of cond`, modeled on `IRConstraintCheck` (~19 touch sites in 11
  files; IRConstraintCheck has no EmitLlvm arm — IRBreakIf needs one or a
  loud refuse). Do not widen `IRForRange` (~30 sites). C++: emitted in
  `genForRangeBinding` (`CodeGenBinding.fs:3847-3902`) — **the break must
  run the per-iteration `frees` first** (rank ≥ 2 slice materialization is
  per-iteration; a naive `break` leaks). Freeze = a second loop copying the
  last written slice forward; abort after. Interp: an `InterpBreak`
  exception in the `IRForRange` arm (`Interp/Core.fs:591-603`), byte-
  identical freeze values or the diff gates go red. LLVM: conditional `br`.
  **Trap**: `unrollForRanges` (`IRMono.fs:1581`) fully unrolls
  literal-bound loops inside poly specialization — decline when the body
  contains IRBreakIf, or a guarded rec array inside an arity-poly function
  silently loses its early exit.
- **AD** (~1h): one refusal at the top of `expandRecArray`
  (`GradExpand.fs:1085`) — a data-dependent stopping ordinal is not
  differentiable v1. (The v2 opportunity stands: `while` declares a fixed
  point, the right structure for an implicit-function-theorem adjoint.)
- **P1 — `converged_at(u)`** (~2d): the stopping ordinal. Structural
  friction: `inferRecArray` returns one block expression; a sidecar can't
  escape it. Route: a side table on the builder (the `MutableArrayLets`
  mold) + statement-level emission, or surface elaboration into two
  statements (PplElaborate already synthesizes ExprRecArray statements).
  Plain intrinsic name, not a keyword. Refuses on unguarded rec arrays.
- **P2 — window riding**: composes with the rec-array ring/snapshot branch;
  under a K+1 ring "freeze" must be redefined (no full trajectory to fill).
  Do not start before P0's interp parity is green.

## 4. Matching on types

The author's three questions, answered by probe:

**"Are we limited to concrete types, or are abstract type matches possible?"**
Abstract matches are possible and are the interesting case — on RANK. Rank is
already a static fact the checker holds (`arityConstraints` via
`LookupOrCreateTypeVar`, `Unify.fs:525-541`; `GetArityConstraint`/
`GetRankLowerBound` are the queries), and `rank(e)` already folds to a literal
at lowering (`Lowering.fs:869-874`). Element type is queryable too; index
identity partially; extents are DELIBERATELY outside type identity ("never
compared in unify") and can never be a pattern. Caveat: a NON-literal `T^r`
rank is rank-ERASED at lowering (`TypeLower.fs:597-602`) — `T^r` is not
rank-polymorphic today, it collapses to whatever the body forces.

**"What happens if we match on array types?"** Today, nothing good, silently:
`Array<Float like I>` / `Idx<3>` / `(v : Float64)` in pattern position are
parse errors, but a BARE type name (`| Float64 ->`) parses as an ordinary
VARIABLE binder — irrefutable, first arm always wins, every later arm dead,
no diagnostic (VERIFIED: emits a constant). The single highest-value refusal
in this arc is rejecting a known type name in pattern position outside an
ascription pattern — a breaking change needing a corpus/examples census first.

**"Can we define a recursive array by matching array types?"** Two different
things hide here. (i) Today's `let rec` is induction on VALUE structure along
the leading axis — it works, including rank ≥ 2, and is not the general match
path. (ii) Type-structure induction is induction on the INDEX-TYPE LIST —
rank recursion. The coherent v1 CONSUMES an array by rank induction
(`flatten_sum` peeling one axis per step, statically unrolled, termination by
strictly-decreasing rank — the packsum argument with rank for arity).
DEFINING an array by type induction needs rank-indexed OUTPUT types, which
breaks in three places (unify has no `T^r` ~ `T^(r-1)` relation; Zonk would
default the open output var; ApplyInfo computes output ranks at typecheck,
before pruning) — a research item, refused loudly in v1.

### The load-bearing precedent

Arity-polymorphic pack recursion IS a static type-ish match already shipped:
`match arity(A) with | 1 -> A[0] | _ -> head + packsum(tail)` (corpus
arity/018) — parsed as an ordinary match on an intrinsic, typechecked ONCE
generically (bidirectional checkMatch pushes the declared return so literal
arms flex), specialized by a worklist (`monomorphizeModule`,
`IRMono.fs:1802-1885`) that spreads the recursive call's pack at the call
site and cascades to the base arm via constant-match folding. Rank dispatch
is the same shape with rank in place of arity.

### Phases

- **P1 — hoist the constant-match fold. LANDED 2026-08-30.** The only
  `IRMatch(IRLit)` fold in the compiler lived inside `specializeFunction`
  (arity-poly bodies only), so `match rank(a) with | 0 -> .. | _ -> ..` on a
  concrete array survived to codegen as `(0L == 0L ? .. : ..)` — a runtime
  ternary over a decided question whose dead arm could be ill-typed.
  `foldConstIntMatch` / `foldConstMatchesModule` (IRMono) now run
  module-wide in Lowering before both back ends; the specializer shares the
  same implementation, which is also ORDER-SOUND where its local copy was
  not (a guarded arm whose pattern matches bails the fold instead of being
  skipped). basic/057 pins arm selection by value.
- **P2 — abstract rank peeling. LANDED 2026-08-30**
  (`claude/dreamy-northcutt-26b1ba`, full suite 5281/0 against a 5277/0
  baseline on the same branch; interp diffs functions 94/0, arity 51/0,
  loops 181/0). THREE synthesis sites assumed rank 1, not one — the census
  in the plan named only the first:
  - `requireArrayArgMinRank` (`TypeCheckSupport.fs:921`), the shared path
    every array intrinsic uses. `extents(x)` on a `T^2` param refused the
    rank-1 array it had itself just minted, one line earlier.
  - `inferReduce`'s `operandRank` probe, which reads only the RESOLVED type
    — so `rk = None` for every caret-pinned operand, and BOTH rank-aware
    routes (leading-axis fold, partial fold) were skipped. `reduce` does NOT
    go through `requireArrayArgMinRank`; this is why the reported symptom
    was the tail arm's "reduce() requires an array as first argument"
    rather than the caret message the shared path gives.
  - `inferReduce`'s tail synthesis (`nSlots`), whose unify failure against
    the pin is discarded (`|> ignore`), leaving the operand unresolved so
    the tail arm blames the caller.

  All three now read `GetArityConstraint`, MAX-joined with the site's own
  demand. With the rank known, the partial fold routes through the existing
  row-mode rewrite — no new IR node, no new codegen.

  `GetRankLowerBound` is deliberately NOT a fallback at any of the three,
  contrary to this plan's original sketch: the caret is a DECLARATION to
  synthesize from, a rank lower bound is accumulated EVIDENCE for `unify`'s
  `rankBoundViolation` to CHECK the synthesis against. Reading the bound
  makes that check vacuous and demotes functions/037's BL3009 (the
  dedicated rank-deduction code) to a BL3001 pointing at an unrelated call
  — measured, the only two reds in the first A/B.

  Corpus: `arity/047` (T^2 peel to T^1, pinned against the hand-written
  row-wise spelling), `arity/048` (T^3 double peel), `arity/049` (the
  `requireArrayArgMinRank` half, via `extents`), `arity/050` (rejects: a
  rank-0 abstract param claims no rank, so `reduce`'s rank-1 default stands
  and the scalar call site is refused).

  DISCOVERED, newly reachable, for P3: chaining two abstract-RANK calls
  with the intermediate left open — `peel2(peel3(c))` — does not work. The
  callee's output extents are the synthesized `__..._inferred_n` params and
  shape monomorphization has no rank worklist to close them over, so the
  binding fails IR validation with BL6001 when USED and is silently PRUNED
  (no output, exit 0) when it is a bare unused top-level `let`. The silent
  prune is the part that needs a refusal even before P3 lands. Ascribing
  the intermediate works and is what `arity/048` pins.
- **P3 — the rank worklist** (the bulk): a rank-keyed specialization pass
  modeled on the arity monomorphizer — seed from concrete call sites,
  specialize per input rank, rescan spec bodies, cascade to the base arm
  via the P1 fold. NOT shape mono: `shapeCallForwardsExtents`
  (`IRMono.fs:2287-2300`) explicitly refuses non-uniform recursion, and a
  rank-peeling call is exactly that.
- **P4 — typecheck the recursive call at the peeled rank**: a
  rank-changing self-call passed `check` monomorphically and emitted
  uncompilable C++ (VERIFIED — a live bug independent of this feature).
  Re-probed after P2: the `T^k`-annotated spelling
  (`function flatten_sum(x: T^2) -> T^0 = match rank(x) with | 1 -> ... |
  _ -> flatten_sum(reduce(x, (+)))`) and the unannotated one now BOTH die
  at `check` with a BL3001 rank mismatch naming both ranks, which is
  already P4's v1 rule for the refusal half. What remains is the
  ACCEPTANCE half — checking the recursive call against the declared
  signature at the PEELED rank rather than the parameter's — plus the arm
  unification relaxation below.
  v1 rule: rank-recursive functions require an explicit signature (the same
  rule `let rec` already enforces), and the recursive call checks against
  the DECLARED signature at the peeled rank. Note the tension with §2 fix
  2: strict arm unification is right for value matches, but rank-dispatch
  arms may legitimately carry per-rank types — P4 must relax arm
  unification exactly when the scrutinee is a static rank/arity literal
  (the arms that survive pruning are the only ones that must agree).
- **P5 — refusals**: type name in pattern position (the silent-binder
  hazard; census first — breaking), `Array<..>`/`Idx<..>` patterns get a
  steering error naming the rank form, unreachable-arm warning after an
  irrefutable arm (§2 open item 2 covers it), non-exhaustive rank arms.
  `PatTyped` ascription patterns (`(v : Float64)`) stay OUT of v1: the
  typecheck arm exists but unifies into the global Subst with no rollback —
  `PushTypeVarScope` does not cover the substitution map, so arm-local
  refinement needs real machinery, not a parser production.

Corpus sketch: `tests/corpus/typematch/` — rank dispatch scalar/array,
flatten_sum (flagship, P3), pruned-emission pin (FlatPathTests-style, since
the corpus can't see emitted text), rejects for the P5 refusals; plus interp
and diff-oracle entries — the twins must agree on every fold.
