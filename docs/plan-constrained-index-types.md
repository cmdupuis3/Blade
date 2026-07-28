# Plan: Constrained Records as Index Types ("static constraint lifting")

> **Status:** design / plan. Speculative tier — not canonical spec. A core-
> language feature (not a transforms-as-types stage), designed at the fifth
> two-agent round of that program (2026-07-27) at the owner's direction:
> "using constrained types and constrained records to implement the CG
> constraints — if you had a static one, that could be lifted to type
> level." Cross-referenced from
> [plan-transforms-as-types.md](plan-transforms-as-types.md) §3.6 (the
> 5b-iii user-declared-groups deferral this unblocks) and
> [features/equivariant-nn.md](features/equivariant-nn.md) §7 (the
> CGPath/CGIndex sketch this makes real).
> Canonicality order still applies: Coq proofs > formalism > compiler > this
> note. **Date:** 2026-07-27.

## 1. Goal

Make the solution set of a statically-decidable constrained record a
first-class index type:

```blade
static struct CGm112 {
    m1:    Int<min=-1, max=1>,   // inclusive; {-1, 0, 1}
    m2:    Int<min=-1, max=1>,
    m_out: Int<min=-1, max=1>
} where m1 + m2 == m_out

let s = reduce(method_for(range<CGm112>) <@> lambda(m1, m2, mo) -> ...)
// iterates the 7 solutions in lex order — never the 27-cell box
```

The struct name IS the index type name. **No new type former** —
you never write `ConstrainedIdx<CGm112>`; "ConstrainedIdx" is documentation
vocabulary only. Nor is `static` a new keyword: it already spells the
compile-time world in `let static`, `static function` and `static method_for`,
and `static struct` uses it to mean exactly that. What it adds is opt-in
index-eligibility (§3.1(c)).

The half-open spelling `m1: Int in -1 .. 2` denotes the SAME field range and
remains fully supported — see §3.1(b) for the two bound spellings and the
translation law between them.

## 2. Background: what is already true (the round's two decisive facts)

1. **Static construction discharge is SHIPPED.** A constrained struct
   folding in the compile-time world runs its conjuncts with field values
   bound and fails the fold on violation (StaticEval.fs:320-392,
   "let-static assertion semantics"); the runtime path guards every
   assignment (TypeCheck.fs:8426 synthesizeStructChecks); both worlds
   share ONE conjunct definition (Ast.fs:685 structConjuncts — "so the
   two worlds cannot drift"). Half the owner's idea is inventory, not
   proposal. The static/runtime boundary is per-evaluation-world (the
   `let static` fence) — no dual-pathing needed.
2. **The Coq story is an instantiation, at the RIGHT citation.** The
   arrow-family enumeration (BladeArrow canonA/enumA) has NO
   prefix-closure hypothesis, and the instance covering ARBITRARY
   full-tuple predicates already exists: BladeCompound.v:50-66's rank-k
   ck arrow (has_completion/ckSt/ck_heads), sound/complete/NoDup for
   arbitrary `M : list nat -> bool`; lex is arrow-general at
   BladeLex.v:119 (enumA_lex_sorted) and inherited by ck at :151
   (compoundk_lex_sorted) — lex is NOT proved in BladeArrow itself.
   Instantiate M := the compiled static predicate over the rectangular
   box and the whole family is proved. (canonA-with-clever-heads was the
   wrong identification; the ck satisfiability-filtered arrow is the
   right one. The ⟦Blade conjuncts⟧ = M eval-model bridge is the one
   deferrable new proof — BladeConstrained.v, named below.)
   Fence-input evidence: StaticEval.fs:193 (maxSteps = 100k),
   StaticEval.fs:10-22 (StaticValue has no closure case — the one-line
   reason route (a) was rejected), CodeGen.fs:7352 (masks are named
   runtime arrays today), Ast.fs:183/548 (TyCompoundIdx's mask is an
   Expr; TyDeclStruct's typeParams are names only — deferral 1's
   evidence).

Also true: CompoundIdx's IxKCompound lowering, mask-hash identity,
residuals, `range<>` iteration, `contains` — the entire consumption side
pre-exists. SparseIdx is formalism-only at HEAD; this plan delivers the
DERIVED half of §3.5's sparse story without implementing explicit entries.

## 3. The mechanism

### 3.1 The surface (NORMATIVE — arbitrated 2026-07-27)

Three rulings settle the declaration syntax. They were arbitrated across the
four implementing threads and are unanimous; the reasoning is recorded because
one of them departs from the owner's literal words and another clarifies a
sentence of §1.

The owner's directive, verbatim:

> "we'll have `where(constraints)` and constraints is either a predicate or an
> array of predicates that only take members as arguments. I think that might
> imply a structure like a limited class, but... the issue is really how we get
> a clean syntax for multiple where constraints without introducing classes per
> se."

**(a) The where-clause: comma-separated conjuncts, three accepted spellings.**

The canonical form is `where p1, p2, p3` — exact parity with the FUNCTION
where-clause grammar that already ships (formalism §5.1: `where comm(xᵢ,xⱼ),
omp(x₁: 2)`). This required no new grammar: `parseConjuncts` (Parser.fs) has
always parsed a comma list after a struct's `where`, and `TyDeclStruct` has
always stored an `Expr list`.

Two further spellings are accepted, honoring the owner's parenthesization and
his "array of predicates":

```blade
where p1, p2        // canonical
where (p1, p2)      // parenthesized
where [p1, p2]      // array
```

They are implemented as one purely syntactic rule in `Ast.structConjuncts`: a
conjunct whose top node is a **literal** tuple or **literal** array flattens to
its elements. This is a *strict widening* — at declaration every conjunct must
unify with `Bool` (`StructWhereNotBool`), so a tuple- or array-typed conjunct is
a hard error today and flattening can only turn errors into acceptances. No
accepting program changes meaning. Because the rule lives in `structConjuncts`,
both evaluation worlds inherit it and cannot drift, which is the invariant that
function exists to protect.

Literal only, deliberately: an array-*valued* expression does **not** fold
all-true. Admitting that would make `where p` and `where (p,)` equivalent for
arbitrary expressions — precisely the drift the single shared definition
prevents.

**Reusable named predicates are plain `static function`s** called in the list:

```blade
where triangle(l1, l2, l_out), parity_ok(p1, p2, p_out)
```

This is the answer to the owner's "limited class" worry. It delivers
"predicates that only take members as arguments" with zero class-like
structure — no attached methods, no `self`, no nominal entity beyond each
function's own name.

**"Members-only arguments" is NOT enforced** — the one deviation from the
directive's literal words. Conjuncts and bounds may reference members *and*
static names. Three reasons: the plan's own anchor needs it (CGm112's bounds
reference `l1`/`l2`/`lo` statics); the shipped bound-scope check already
permits statics, static callables and known builtins, so a members-only rule
would be a *regression* rather than a new check; and the owner's concern was
structural — don't grow a class — which the `static function` route discharges
structurally. Unresolvable free names still fail resolution exactly as before.

**Conjunct order is a pinned contract.** `structConjuncts` is
`declared @ boundConjuncts`: declared where-conjuncts first in written order,
then desugared bound conjuncts in field-declaration order, Lo before Hi.
1-based and identical in both worlds, so `(static, conjunct 2)` and
`(conjunct 2)` name the same predicate. Flattening happens *in place within the
declared list*, before bounds are appended. A field with only one endpoint
yields one conjunct and shifts every later index.

**(b) Field bounds: two spellings, and they mean different things.**

```blade
f: Int in lo .. hi          // HALF-OPEN:  lo <= f  &&  f <  hi
f: Int<min=A, max=B>        // INCLUSIVE:  A  <= f  &&  f <= B
```

Translation law, for Int fields: `in lo .. hi` ≡ `min=lo, max=hi-1`.

The general form is `Base<Unit, min=e1, max=e2>` — positional args first (the
existing unit/tag arguments), then named `min=`/`max=` in either order, at most
one of each, at least one present. `Float<velocity, min=0, max=1>` composes
with units. Bound expressions use the same static-payload grammar as `Idx<n>`:
literals, negative literals, `let static` names, `+ - * /` and parens. On a
struct field the new spelling normalizes at parse time into the existing
`FieldBound` channel (which gains `HiInclusive`), so exactly ONE bounds
representation reaches both worlds. A field carrying both spellings is a decl
error.

Inclusivity changes the *operator only* — it never merges or drops a conjunct,
so a struct written either way has the same conjuncts at the same indices. A
field with only ONE endpoint yields one conjunct, which shifts every later
index — worth knowing before pinning conjunct numbers in a test.

Crossed bounds (`min=3, max=1`) are an ERROR: since both ends are inclusive
there is no reading under which that is a deliberate empty range. The
half-open `in 0 .. 0` stays accepted, deliberately — a `..` range whose ends
coincide is the ordinary way to write "empty" language-wide, and empty
solution sets are a warning-class event here, not a failure.

**Where bounds are enforced.** Bounds erase at lowering — there is no IRType
carrier — so every enforcement channel is synthesized from the SURFACE
annotation. Three cases, and they differ:

| Position | Enforced |
|---|---|
| Struct field | **Both worlds.** Normalizes into `FieldBound` and hence into the shared conjunct list: static fold at `let static`, synthesized guard at runtime. |
| `let` binding annotation | **Runtime.** `synthesizeBoundChecks` over `Ast.boundedConjuncts`, emitted as a post-check on the binding. Message names the binding and the endpoint: `Bound violation in 'x' (max)`. |
| Params, returns, reassignment of a bounded mutable | **Deferred.** Parses and checks; not enforced. |

The `let` row was closed during this round and supersedes an earlier note here
that recorded the whole non-field case as unenforced. What remains deferred is
only the third row, and the reason it is narrower than it looks is that a
bounded `let` is where the value actually enters the program; a parameter
re-states a bound its caller's binding already carried.

> **Observation, not a deferral of ours.** `let n = 4` defaults to Int64, so
> `let x: Int<min=0, max=3> = n` is refused for the width, where the same
> annotation with a bare literal succeeds. This is the literal-default rule
> meeting a narrower annotation, and the implementing thread was right to
> decline widening it at the use site: an annotation silently narrowing an
> already-bound value is an implicit conversion, and the bounded-primitive
> feature is the wrong place to introduce one. If it is ever addressed it
> belongs at the literal-default rule, not here.

Why inclusive, when `..` is half-open everywhere else: `..` is punctuation for
a RANGE and reads half-open language-wide (ranges, subset, `BoundedIdx`), while
`min=`/`max=` are named BOUNDS and read closed — a `max` that excludes its own
value is a lie in the identifier. formalism §2.4 had already specified
`Float<min=0, max=1>` as the closed unit interval before this round, so this
implements the spec rather than inventing a convention. And the motivating
domain object is m ∈ [−l, l] with card 2l+1; writing that as `max=l+1`
manufactures exactly the off-by-one the C2 offset-hazard build exists to catch.
Making `..` inclusive instead was rejected outright: it would silently change
every shipped program that uses a field bound, whereas `min=`/`max=` does not
lex at HEAD and so breaks nothing.

**(c) Index-eligibility is OPT-IN: `static struct`.**

A plain `struct` is never index-eligible, however eligible its shape. This is
the fence's first condition, checked before field types or bounds.

Two reasons decided it against structural eligibility. First, **the emptiness
warning**: a derived-empty solution set warns (below), and that warning is only
meaningful for a struct the user *intended* as an index — under structural
eligibility it would fire at ordinary constrained records that never asked to
be index types, turning a real signal into noise. Second, **no action at a
distance**: adding a bound to the last field of an unrelated struct would
silently promote it, and removing one silently demote it, which is spooky in
exactly the place this design demands deliberate nominal identity.

The marker is purely additive — a `static struct` is an ordinary struct in
every other respect (runtime construction, guards, functional update, field
access all unchanged); `static` only *adds* eligibility.

**Semantics: one conjunct list, two readings.** At CONSTRUCTION, a false
conjunct is an error (§2.4's assert-not-solve, unchanged, both worlds). At
ENUMERATION (the record used in index position), a false conjunct is
EXCLUSION — membership. The decidability fence is what legitimizes the
solve-by-enumeration reading.

**Lowering.** A fence-eligible struct in index position lowers to the
existing `IxKCompound` with a compiler-materialized mask constant over the
shifted box, PLUS:
- an OFFSET VECTOR: record boxes are shifted (`m1 ∈ [-l1, l1]`);
  iteration emits field VALUES (coordinate + lo), and each per-field
  iteration variable carries the field's `Int<min,max>` as its §3.10
  unit (negative values — NOT `Nat<>`). The C2 hazard-build candidate.
- NOMINAL identity by struct name layered on the mask hash (structs are
  nominal; the IrrepsIdx nominative precedent; no anonymous form exists
  so no named-vs-anonymous question arises).
- Rank = #fields (compound semantics: the signature matches mask rank,
  currying passes through; declaration order = lex nesting order, first
  field outermost). Residual/partial-indexing machinery inherits.

**The fence (v1 eligibility; each failure a named diagnostic):**
- the struct is declared `static struct` (§3.1(c)) — checked FIRST, before
  fields, so the common mistake gets a one-line fix rather than a puzzle;
- at least one field;
- every field `Int` with static min/max (literals or `let static` names);
  reject: non-Int fields ("non-enumerable field type"), missing bounds
  ("unbounded field"), non-static bound expressions;
- conjuncts = whatever StaticEval.evalExpr folds to SVBool under the
  existing fuel (100k steps per cell): int arithmetic, comparisons, bool
  ops, if/match, static-function calls. Fuel exhaustion names the WITNESS
  CELL ("conjunct of R did not fold at (m1=-1, m2=0, ...)");
- box cap ∏(hi−lo) ≤ 100,000 cells (the symLiftDecl precedent);
- same-record dependent bounds (`l_out: min=abs(l1-l2)`) are out of the
  BOX grammar but not out of reach: they desugar to conjuncts given
  absolute bounds — a tight-heads efficiency deferral, not a semantics
  gap.

**Enumeration/cardinality:** lex over the shifted box, filtered
(order-preserving shift ⇒ lex-over-coordinates = lex-over-values); storage
offset = enumeration position; cardinality is computed (extents are
values — §3.1.1; this one is a static value), asserted card = |entries| on
every call.

**Emptiness:** a derived-empty solution set is a WARNING (not an error —
Schur-zero-style emptiness is legitimate; not silent — the asymmetry with
`Idx<0>` is intent: literal zero is deliberate, derived zero means the
user believed their constraints satisfiable and a `range<R>` no-op loop is
a silently degenerate program). Downgradeable if noisy; harness-compatible
(6a established warnings trip nothing).

**Sizing surface:** `idx_card(R)` static builtin, bare-identifier argument
(the derive_pg_linear GROUP-argument resolution precedent:
declaration-table lookup). Ships in C1 — corpus static pins are the
counting layer's verification story.

## 4. What consumes it (capability-first; the anti-toy case)

Compiler internals (tpPaths/allValidOutputs/polyLabels) STAY F# — proven,
gated, byte-frozen surfaces. v1 is the capability: user-space constrained
index types + iteration + the emptiness warning. Real demand it serves:
1. equivariant-nn.md §11b's F1 reservation discharged — `CGIndexComplex`
   (the m-selection RULE, "reserved, never shipped") becomes a writable
   type; the F1 split maps exactly onto derived-mask (equation) vs
   data-mask (the real-basis nonzero support = CompoundIdx from data);
2. 5b-iii user-declared groups: "a user-space static table with a
   validity predicate" is exactly this and nothing else shipped provides
   it;
3. the §3.5 sparse story's derived half, without SparseIdx;
4. BL4007-as-inhabitation stays NARRATIVE until internals migration
   (demand-ordered) — the core's own signal is the emptiness warning.
5. (Added post-round, owner Q&A:) **structured irreps iteration.**
   IrrepsIdx is the rank-1 linearization of a spec-derived
   dependent-bound compound (μ < mult(b), m < dim(b)) whose lex
   enumeration COINCIDES with the flat blockStart + μ·dim + m layout —
   so the long-deferred `for (b, mu, m)` sugar / `blocks<IrrepsIdx>`
   virtual iteration should be delivered as a C2-instantiation of the
   derived-compound machinery (zero index translation), NOT as bespoke
   DepIdx iteration codegen. The spec tag stays load-bearing regardless:
   parity is invisible to the mask ([(1,e,1)] ≡ [(1,o,1)]
   combinatorially), so nominal identity cannot be replaced by the
   mask hash — which is exactly this plan's own
   nominal-on-top-of-hash rule, rediscovered.

## 5. Worked anchor (the round's E-plane equivalent)

CGm112 above (`static struct`, per §3.1(c)): box 27, solutions **7**
(m_out = −1/0/+1 with 2/3/2 pairs).
Pins: card 7; the lo-sweep 3/7/9 (lo = 0,1,2; 9 = (2l1+1)(2l2+1)
saturation) with the sum-check against the dense pair count; lex iteration
value pins; the emptiness twin (conjuncts pinning an unsatisfiable parity
combination → card 0 → the warning). Independent oracle: an F# triple-loop
count sharing no code, deliberately NOT MLSpec (this is user-space).

**The emptiness warning belongs to C2, not C1.** Card 0 is C1-legal and
`idx_card` folds to 0 successfully with no diagnostic — C1 is the counting
half, and the warning is only meaningful once the type and its `range<R>`
iteration exist. The C1 corpus asserts the warning-free path explicitly so
that a warning appearing early is caught as a staging error.

**The both-spellings differential** (added with §3.1(b)): the anchor is
declared twice, once half-open and once inclusive, and the two must agree on
cardinality AND lex order. It costs no new oracle and catches an inclusivity
error in either desugar — an off-by-one breaks exactly one of the pair. The
sharpest offset probe, though, is an ASYMMETRIC negative-leaning box
(m1 ∈ [−2, 0], m_out ∈ [−3, 1]): the anchor is symmetric about zero, so a sign
error in the coordinate→value shift can cancel there, and only an asymmetric
box turns it into a visibly wrong value rather than a reordering.

## 6. Open questions / risks

1. The ⟦conjuncts⟧ = M eval-model bridge (BladeConstrained.v) — deferred;
   the ck theorems pre-exist and nothing v1 ships depends on the bridge.
2. Per-field negative-value units through §3.10 (the offset shift) — the
   C2 hazard build must demonstrate the wrong-shift bug fires before the
   fix is trusted.
3. ~~Fuel-bomb ergonomics: a recursive static function in a conjunct burns
   100k steps PER CELL before diagnosing — acceptable at the box cap;
   revisit if real programs hit it.~~ **FALSIFIED at C1 implementation
   (2026-07-28) — see §7.0(3) for the finding, the repro and the repair.**
   The premise is wrong in the direction that matters: the fuel bound does
   not diagnose, it dies, and the box cap does not bound the damage because
   a single cell is enough to reach it. This entry is struck rather than
   deleted because "acceptable at the box cap" was the reasoning that let
   the risk be accepted, and that reasoning is what failed.

## 7. Implementation staging (counts before types before ergonomics)

- **C1 — the counting half (no types, no emission).** `StructIdxSpec`
  module beside MLSpec: eligibility check, box enumeration, conjunct
  evaluation via StaticEval, entries + card; `idx_card(R)` builtin;
  asserts on every call — flat-filter count AND ORDER = arrow-style
  heads-filtered count (two genuinely different algorithms; order
  agreement catches offset bugs — the 5a-i third-route discipline);
  closed-form pins (unconstrained card = box volume; the 3/7/9 sweep);
  negative controls (box cap, non-static field, fuel bomb) → pinned
  diagnostics.
- **C2 — the type.** Lowering arm (struct in index position →
  IxKCompound + baked mask + offsets + nominal tag), `range<R>`
  iteration emitting field values with per-field units, the emptiness
  warning, unify rules (nominal; distinct structs never unify). Corpus:
  accept/value/reject/emptiness; the offset hazard build.

  > **Inherited coverage — do not prune.** `structs/050` and
  > `diagnostics/032` read as bounds tests and are not. A struct in index
  > position is a struct whose conjuncts are read at FOLD time, so the
  > static-dependency edge they pin (naming a type pulls in the statics its
  > bounds and conjuncts name — §7.0(2)) is the mechanism C2 leans on
  > hardest. They are load-bearing for a feature that does not exist yet,
  > which is exactly why they are at risk: whoever tidies the bounds corpus
  > will find two files that look redundant with `structs/046-047` and are
  > not. `structs/050`'s value is entirely in its SOURCE ORDERING (the
  > static declared below its use), which is invisible to a casual reader
  > and survives no reformatting.
- **C3 — ergonomics + closure.** Field-named access/destructuring in
  iteration, record-valued full application `A(e)`, residual pins on
  baked masks, the user-declared fusion-table example, the formalism
  write-up (§3.5 "derived masks" subsection + §15.4/§5.4 cross-refs +
  the two-readings sentence).

**Formalism amendments are OUT OF SCOPE for the C1 round** and stay a C3 item.
§3.1's rulings are normative *here* and in the compiler, but the canonicality
order (Coq > formalism > compiler > this note) means the surface syntax is not
spec until C3 writes it up. Three amendments are owed at that point: §2.4's
bounded primitives gain the Int case and the explicit statement that
`min=`/`max=` are inclusive while `..` is half-open; §5.1's where-clause
grammar gains the struct-level conjunct list and its three spellings; and §3.5
gains the "derived masks" subsection. Recorded here so C3 inherits a list
rather than a rediscovery.

### 7.0 C1 — LANDED (2026-07-27)

Full suite **2009 passed / 0 failed** (baseline 1872/0 at the round's HEAD),
run from a private working directory. That last detail is load-bearing rather
than fussy: the C++ test output directory is CWD-relative and hardcoded
(`./generated_cpp_tests`) at ~15 sites with no flag or environment override,
so concurrent runs sharing the repo root contaminate each other's artifacts.
A suite number from a shared root is not evidence.

> **Reading a red suite during a parallel round.** Interference does not look
> like interference; it looks like a regression. The signature is a SCATTERED
> set of failures across UNRELATED lanes — during this round, an interfering
> run produced reds in the mpi clause test, Reynolds AntiSym, GroupBy Mixed
> Kernel and `index-types/146` simultaneously, which no single change could
> explain. The diagnostic is to re-run the suspect test ALONE: `146` solo gave
> 3/7/9 with DENSE 9, SATURATES 0, D10 4, D21 2 and was clean. Check for
> another suite in flight before believing a scattered red set, and prefer a
> targeted re-run to a second full suite — a competing gate manufactures the
> very evidence it is trying to evaluate.
>
> **Stale corpus binaries are the same trap with a nastier signature.** A test
> run leaves compiled `.exe` files NEXT TO the corpus sources
> (`tests/corpus/index-types/146_....exe` and friends). They are gitignored,
> so they never reach a commit and are easy to forget — but a later run reuses
> them, which means a build you have already reverted can keep executing. This
> bit hard during a mutation-testing pass: after reverting the mutant and
> rebuilding, exactly the four adversarial key-order pins failed, and the
> obvious reading — "the resolveStatics edges are broken under adversarial
> naming" — was wrong. Stale mutant binaries were being re-run. The signature
> is worse than the concurrency one because it is NOT scattered: it is a
> tight, plausible, thematically-related cluster, which is precisely what a
> real regression looks like. Before believing any post-revert failure, clean
> the tree and `find tests/corpus -name '*.exe'`. Private run directories are
> necessary but NOT sufficient: a fully-isolated sweep run concurrently with
> two others still produced 13 reds purely from resource exhaustion
> (`cc1plus.exe: out of memory`, `CreateProcess` spawn failures, exit
> 0xC0000142) — each suite spawns a parallel g++ fleet, and three fleets
> exceed the machine. File isolation prevents corruption; only
> SERIALIZING full suites prevents false reds.

What shipped:

- `src/StructIdxFence.fs` — declaration-time eligibility, `static struct`
  first; `src/StructIdxSpec.fs` — box enumeration with the internal
  flat-vs-heads certificate; the `idx_card(R)` static builtin.
- Surface per §3.1: the three where-spellings, `Base<Unit, min=, max=>`
  inclusive bounds alongside the unchanged half-open `in lo .. hi`, and
  `static struct`.
- Verification: `Struct Idx` 37 and `Struct Idx Oracle` 72 (the third route,
  §7.1), plus 28 corpus files — 8 `structs`, 3 `struct-aborts`, 5
  `diagnostics`, 11 `index-types`, 1 `basic`.

Three things surfaced that were not in the plan and are worth carrying
forward. Two are bugs found and fixed en route; the third is a landmine left
deliberately intact, with a note so the next reader does not walk into it.

1. **A pre-existing negative-literal bug**, fixed en route. `P { a = -1 }`
   was refused ("expected Int32, got Int64") while `P { a = 1 }` succeeded — a
   bare int literal was retyped to the expected scalar during checking, but a
   NEGATED literal fell through to inference. Unrelated to bounds or index
   types, but every m-value family here is symmetric about zero, so it blocked
   the anchor. The fix is narrow (literal operand only, not general
   bidirectional propagation) and turns only errors into acceptances, but it
   sits in a maximally-shared path — regression probe at corpus
   `structs/049`, deliberately written as a plain unbounded struct so it keeps
   protecting the fix after this feature moves.
2. **A static-ordering bug, found and FIXED in the round.**
   `Int<min=-LO, max=LO>` with a `let static LO` was refused on an
   index-eligible struct ("undefined variable 'LO'") while the identical
   declaration constructed and folded fine as an ordinary record.

   The symptom looked like a missing environment; it was not. `let static C =
   idx_card(R)` mentions no static — free-name collection sees `idx_card` and
   `R` and stops — so the static dependency graph gave `C` no edge to `LO`,
   and the topological sort was free to fold `C` first, when `LO` genuinely
   was still undefined. Literal bounds worked only because they need no edge.
   Worth recording because the misdiagnosis was reasonable and the real cause
   generalizes: **naming a type can create a static dependency**, since the
   type drags in whatever its bounds and conjuncts name.

   Fixed in `StaticEval.resolveStatics`: mentioning a struct in a static
   expression pulls in the statics its field bounds and declared conjuncts
   name, minus the field names. Deliberately general rather than
   `idx_card`-specific — the edge is a property of mentioning the type, not of
   which builtin consumed it — and transitive for free, since the edge lands
   on a static name carrying its own dependencies. `index-types/146` declares
   `let static LO` BELOW the struct that uses it, so source order cannot be
   what makes it pass.

   The generality is itself pinned, because a point-fix would have passed
   every test that existed when the bug was found. The struct-LITERAL path
   needs the identical edge — `Plain { m = 2 }` names `Plain` and `m` and no
   static at all — and it is a SEPARATE collection seed, so it can regress
   independently of the consumption path. `structs/050` folds a literal whose
   bound forward-references a static; `index-types/155` is its `idx_card`
   twin; and `diagnostics/032` pins that the resolved bound is still ENFORCED
   (at the right conjunct index) rather than merely resolved — the failure mode
   a half-fix leaves behind, where making the name visible and dropping the
   check passes the accept case.

   > **Pin contract for this fix: ADVERSARIAL KEY-ORDER NAMING.** Static
   > resolution drains a Map in KEY order, so a missing dependency edge is
   > invisible whenever the depended-on static happens to sort BEFORE its
   > consumer — the resolver stumbles into the right order and the test passes
   > with the edge logic deleted. Source order is necessary and nowhere near
   > sufficient: the resolver does not read top-to-bottom. Every pin in this
   > family therefore names its consumers `a_*` and its static `zz_*`, so the
   > consumer sorts first under any comparer. This was learned the hard way —
   > the first drafts of `structs/050`, `diagnostics/032` and the F# pin in
   > `Test_StructIdxSpec.fs` all sorted the static first by accident (`LO`
   > against `top`/`bad`, `L` against `CARD`; ordinal comparison puts
   > uppercase before lowercase), so each passed for the wrong reason. A
   > future rename to something descriptive silently restores that vacuity.

   **The coverage is DEMONSTRATED, not argued — a hazard build was run.**
   Adversarial naming is only an argument that a pin can fail. The argument
   was discharged by disabling the edge itself (one line in
   `resolveStatics`: `refs = direct` instead of `Set.union direct
   structRefs`), rebuilding, and confirming that all five pins fail, each
   with a message naming the real cause:

   | Pin | Failure under the hazard build |
   |---|---|
   | `Test_StructIdxSpec` adversarial-key-order | `field 'm1': min bound is not static — undefined variable 'zz_lim'` |
   | `index-types/146` | Idx Card Lo Sweep |
   | `index-types/155` | Idx Card Static Bound Key Order |
   | `structs/050` | Struct Static Bound Forward Ref |
   | `diagnostics/032` | no message contains `Constraint violation in Plain (static, conjunct 2)` |

   Two things fall out that naming alone could not establish. First,
   `index-types/146` is adversarial IN FACT and not merely by the accident of
   `CARD2` sorting before `LO` — it fails under the hazard. Second, and more
   structurally: **one line disables BOTH seeds**, because the struct-literal
   collection path and the `ExprVar` mention path feed the same `structRefs`
   fold. `structs/050` and `diagnostics/032` failing under the SAME disable is
   direct evidence that the construction and consumption paths share one
   closure rather than merely appearing to — which is the property §7.0(2)
   claims and could not otherwise show. The edge was restored byte-exactly
   from a pre-probe backup (CRLF preserved; a normalizing restore was
   discarded rather than shipped as whole-file churn) and re-verified
   independently.

3. **The fuel-bomb negative control of §6 risk 3 is NOT PINNABLE**, and the
   reason is pre-existing and unrelated to this feature. `StaticEval.maxSteps`
   is threaded as `fuel - 1` into each CHILD, so it bounds evaluation DEPTH,
   not step count — and 100,000 nested `evalExpr` frames overflow the .NET
   stack long before the counter reaches zero. An unbounded recursive static
   function therefore kills the compiler with an uncatchable
   StackOverflowException instead of producing the fuel-exhaustion diagnostic,
   which is consequently unreachable for exactly the case it was written for.
   Reproduced with no struct and no `idx_card` involved:

   ```blade
   static function bomb(n: Int) -> Int = bomb(n + 1)
   let static X = bomb(0)
   ```

   Left unfixed as out of scope: the honest repair is an explicit depth
   counter well under the stack limit, or a genuinely threaded step budget —
   a StaticEval decision, not a counting-layer one. The half C1 does own, the
   WITNESS CELL on a conjunct that fails and returns, is pinned instead
   (`index-types/154`). §6 risk 3 should be re-read with this in mind: the
   ergonomic concern it raises is real but its premise — that the fuel bound
   diagnoses — does not hold today.

### 7.1 C1 verification inventory

What the counting half is checked BY, as distinct from what it does. Three
independent routes plus hand tables:

1. **StructIdxSpec's internal certificate** — flat box-filtering vs
   arrow-style heads-filtering, asserted equal as set AND order AND card on
   every call. Two genuinely different algorithms, but written together and
   sharing the module's own notion of what a box is.
2. **The third-route oracle** (`tests/Test_StructIdxOracle.fs`) — recursive
   per-field extension over VALUES, sharing no code with the module and never
   forming a coordinate, so it cannot inherit a shift error. It compares ORDER
   (list equality) and SET *separately*, so a convention failure and a
   membership failure are distinguishable; it recomputes card and separately
   asserts `Card = |Entries|`, which is what stops a future closed-form
   cardinality from drifting away from the list it claims to describe.
3. **Hand tables** — the anchor and the asymmetric box have their lex-ordered
   entries written out by a human. Two agreeing programs can still both be
   wrong; a hand table is the only check with a genuinely independent source.
4. **Corpus static pins** — `idx_card` folded values: the anchor 7, the
   unconstrained volume 27, the 3/7/9 sweep with its saturation identity
   against the dense pair count and its +4/+2 difference structure, the
   card-0 emptiness case with a non-empty control, and both bound spellings
   agreeing.

Negative controls, each a named diagnostic: not-`static struct`;
non-enumerable field type; unbounded field; unknown struct name; a bound
naming an earlier field (deferral 5); the box cap (pinned on a struct with
exactly ONE solution and a 1,030,301-cell box, so "the cap is on the BOX, not
on the answer" is the test rather than a comment); the witness cell on a
conjunct that fails to fold, whose reported cell is the first failing one in
LEX order — a commitment, pinned on a conjunct that folds for small values and
fails past them, so the witness is not the origin; and — the one that matters
most — an UNDECIDABLE cell must FAIL the enumeration rather than be treated as
false. Conflating "cannot decide" with "false" silently shrinks solution sets,
which is a wrong answer that looks like a right one.

Not pinnable: the fuel bomb, for the pre-existing StaticEval reason in §7.0(3).

## 8. Honest deferrals (by name)

1. **Value-parameterized structs** (`CGIndex<path: CGPath>` — the §7
   sketch verbatim): TyDeclStruct's typeParams is names-only; the real
   blocker. Shares the constructor-promotion PROBLEM with 5b-iii but not
   scope: this is a core grammar/registry change that would unblock
   `CGIndex<path>` regardless of whether user-declared groups ever land —
   the dependency arrow points FROM 5b-iii TO this deferral, not the
   reverse.
2. Sum-type fields (`Parity`): StaticValue has no variant case; int
   parity is the shipped ops' own convention meanwhile.
3. Field-named iteration / record-valued indexing (C3).
4. Residual verification on baked masks (machinery inherits; pins C3).
5. Same-record dependent bounds as tight heads (efficiency only).
6. Internals migration (tpPaths et al.) + BL4007-as-inhabitation.
7. `CompoundIdx<static mask expr>` (route (a)) — needs a static-closure
   world; orthogonal, if ever.
8. BladeConstrained.v (the eval-model bridge; a dependent-heads ck
   variant if (5) lands).
9. SparseIdx explicit-entries lowering — untouched, formalism-only.
10. **Static-function CALLS in bound position** (`Int<min=lo_of(l1, l2)>`).
    Bound expressions use the `Idx<n>` static-payload grammar — literals,
    negative literals, `let static` names, arithmetic and parens — which does
    not include calls, exactly as `Idx<f(2)>` does not parse at HEAD. Shared
    limitation, not a rule about bounds; lifting it lifts both. Conjuncts are
    unaffected: a static-function call is fully available there (§3.1(a)), so
    the capability is reachable today by writing an absolute bound and letting
    the conjunct do the narrowing — the same workaround deferral 5 relies on.
