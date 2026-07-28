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
struct CGm112 {
    m1:    Int in -1 .. 2,      // half-open; {-1, 0, 1}
    m2:    Int in -1 .. 2,
    m_out: Int in -1 .. 2
} where m1 + m2 == m_out

let s = reduce(method_for(range<CGm112>) <@> lambda(m1, m2, mo) -> ...)
// iterates the 7 solutions in lex order — never the 27-cell box
```

The struct name IS the index type name. No new former, no new keyword —
"ConstrainedIdx" is documentation vocabulary only.

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

## 5. Worked anchor (the round's E-plane equivalent)

CGm112 above: box 27, solutions **7** (m_out = −1/0/+1 with 2/3/2 pairs).
Pins: card 7; the lo-sweep 3/7/9 (lo = 0,1,2; 9 = (2l1+1)(2l2+1)
saturation) with the sum-check against the dense pair count; lex iteration
value pins; the emptiness twin (conjuncts pinning an unsatisfiable parity
combination → card 0 → the warning). Independent oracle: an F# triple-loop
count sharing no code, deliberately NOT MLSpec (this is user-space).

## 6. Open questions / risks

1. The ⟦conjuncts⟧ = M eval-model bridge (BladeConstrained.v) — deferred;
   the ck theorems pre-exist and nothing v1 ships depends on the bridge.
2. Per-field negative-value units through §3.10 (the offset shift) — the
   C2 hazard build must demonstrate the wrong-shift bug fires before the
   fix is trusted.
3. Fuel-bomb ergonomics: a recursive static function in a conjunct burns
   100k steps PER CELL before diagnosing — acceptable at the box cap;
   revisit if real programs hit it.

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
- **C3 — ergonomics + closure.** Field-named access/destructuring in
  iteration, record-valued full application `A(e)`, residual pins on
  baked masks, the user-declared fusion-table example, the formalism
  write-up (§3.5 "derived masks" subsection + §15.4/§5.4 cross-refs +
  the two-readings sentence).

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
