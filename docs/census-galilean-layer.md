# Census: where should the GALILEAN discipline check and deduce?

Measured 2026-07-29 on branch `g1-galilean`, at `18b1f06` + this branch's
harness. Instrument: `tests/Test_GalLayerCensus.fs`, verb
`blade test gal-layer`. Every number below is reproduced by that block on every
run; nothing is estimated.

**Scope.** This answers, for GALILEAN alone, the question
`docs/design-discipline-as-data.md` poses for all three equivariance-family
disciplines. It follows the decision that the disciplines are NOT unified:
`DisciplineKit` is a library galilean may call, not a framework it must
instantiate, and **galilean does not inherit equiv's answer**. Nothing here
changes any checking behaviour, moves any gate, or touches
`src/ml/compiler/MLGalilean.fs`.

**Claim labelling.** Every claim is **[MEASURED]** (produced by the harness, by
a probe run against the compiled binary, or by reading the code path it
exercises) or **[ARGUED]** (a judgement about what the measurement implies).

**Honesty about the instrument.** The typed galilean judgment used here is
EXPERIMENTAL and lives in the test assembly. Nothing in `src/` references it; it
emits no diagnostic; it is not in the full suite. It is a measuring instrument,
not a shipped discipline.

---

## 0. Headline

| Question | Answer |
|---|---|
| Galilean certificates in the corpus (source pins) | **28** [M] |
| …in files the seam ACCEPTS | 17 |
| …in files the seam REFUSES | 11 |
| Plus `SgsElaborate` stamps reaching typecheck in accepted files | 6 |
| **Acceptance census** (23 certificates) | **19 confirm / 4 abstain / 0 disagree** [M] |
| …source-written | 17: **16 confirm / 1 abstain** |
| …generated stamps | 6: 3 confirm / 3 abstain |
| **Rejection census**: galilean-channel reject files | **11** [M] |
| …offender verdicts where the typed side would **still refuse** | **1 of 11** |
| …with a "preserves" claim added | **2 of 11** |
| **Deduction differential** vs the seam's BL4014 channel | **7 matched / 0 seam-only / 0 typed-only** [M] |
| `tests/corpus/diagnostics` BL4009 `ERROR-CONTAINS` pins surviving a flip | **0 of 3** [M] |
| BL4009 span pins surviving | 0 of 2 [M] |

**The verdict, in one line [ARGUED]:** *galilean's DEDUCTION should move to
typecheck and its CHECKING should not — the same shape equiv reached, but for
almost entirely different reasons, and with the deduction half a far stronger
result than equiv's.* Typed deduction reproduces the seam's inference channel
**exactly** (§5), which equiv's differential never achieved. Typed checking, by
contrast, would silently accept 10 of the 11 programs the seam refuses (§4) and
would lose all three of the corpus's worded BL4009 pins (§6), and — unlike equiv
— it would gain **nothing** in return, because galilean's judgment reads no
types at all (§2).

**The most useful negative result:** galilean's checking is not held at the seam
by the reason equiv's is. Equiv stays because its evidence (the surface `ml`
vocabulary) is *gone* by typecheck, measurably, in 6 of its 30 rejections.
Galilean's evidence survives the move almost intact — the typed walker judges
the same bodies to the same answers, 0 disagreements over 23 certificates, and
every rejection in the corpus turns on a rule that still applies at typecheck.
What galilean would lose is, with one named exception (§4, family C), not
evidence but **diagnostics**: 33 refusal messages with sub-expression spans,
replaced by a status value that carries neither. That is a strictly cheaper
problem than equiv's and it is fixable in one place (§7).

---

## 1. The incumbent, characterized

### 1.1 The five axes

**[MEASURED]** by reading `src/ml/compiler/MLGalilean.fs` (632 lines).

| axis | galilean |
|---|---|
| **Status lattice** | `BVar \| BInv \| BOpaque`, plus `Error` in a `Result` (`fs:52-55`). No refinement of "fixed" — the design doc's `'Fix = unit`, and it really is unit. |
| **Classifier** | The conjunct's parameter NAME list (`fs:103-107`). **No types are read anywhere.** |
| **Transfer rules** | An AFFINE SHIFT `u ↦ u + U₀`. `Cov − Cov → FIXED` is the central rule; `Cov + Cov`, `−Cov`, and `scalar·Cov` are all rejects. |
| **Claim vocabulary** | `__ml_galilean(p, …)`, a `WhereClause.Custom` conjunct; suggestions on `GalCertSuggestions` (BL4014); errors BL4009; structured facts on `Equiv.CertFacts` with `Discipline = "galilean"`. |
| **Mode** | Import-gated only. Hypothesis space = subsets of the parameters (singletons that occur free; else the full occurring set once). **Every** passer is proposed, not the strongest. |

### 1.2 (a) The annotation gate — VERIFIED, and it survives the move

The survey says galilean has no annotation gate. **[MEASURED]**, by probe
against the compiled binary:

```
function shear(u, v) where ml.galilean(u, v) -> Float = u - v      →  OK
function bad(x, w)   where ml.equiv(O3)      -> Float = 1.0        →  BL4008
    "an equiv-certified function must annotate every parameter and its
     return type ('x' is unannotated)"
```

`MLEquiv.certSigOf` refuses an unannotated parameter outright
(`MLEquiv.fs:430-431`); galilean has nothing to refuse, because it reads names.

**What it buys [ARGUED]:** recall on partially-annotated code, and — more
importantly — it removes the entire *category* of failure that R1's census
records as equiv's family D (two ml-equiv files refused at the classifier before
any body is walked). Galilean's classifier has **no failure case at all**: every
name either is or is not a parameter, and the "is not" case is a malformed
conjunct refused before any body is judged.

**What would happen to it at typecheck: nothing.** **[MEASURED]** — the
harness's probe (a) runs the same unannotated source through typecheck and the
experimental typed walker CONFIRMS it. Parameter names and binder ids exist
whether or not a type was written, so the gatelessness is layer-independent.

This is the first place galilean and equiv diverge on the layer question. Equiv's
strongest single argument for moving is that the exact types are at typecheck.
**Galilean has no use for them.** In the typed instance built here,
`StatusOps.FixOfType` and `StatusOps.ClassifyTy` — the two hooks through which
the kit offers a discipline the typed win — are both **constant functions**.

### 1.3 (b) Boost-variance is not a type property — CONFIRMED

MLGalilean's header states the premise (`fs:17-20`):

> Units are deliberately NOT the seed: a velocity DIFFERENCE still carries the
> velocity unit but is boost-invariant.

The survey infers from this that a per-type classifier cannot serve galilean.
**The inference is correct, and it is now MEASURED rather than argued.** Probe
(b) in the harness runs two programs with the SAME signature and the SAME body,
differing only in the certificate:

```blade
function j(a: Float, b: Float) where ml.galilean(a, b) -> Float = a - b   // holds
function j(a: Float, b: Float) where ml.galilean(a)    -> Float = a - b   // does not
```

**[MEASURED]** typed verdicts: `confirm` and `disagree`. A function
`IRType -> status` is given the same input in both cases and must produce both
answers. It cannot. **Classification for galilean must be signature-level**, and
`design-discipline-as-data.md` §4.2's correction to the plan stands.

The corpus already carries the strongest form of this, and both halves confirm:
`084_suggest_galilean_independent_singletons.blade` declares `flux_pair_u` and
`flux_pair_v` with **identical signatures and identical bodies** under
`galilean(u)` and `galilean(v)` respectively. Both are true theorems; both
`confirm`. Two different laws on one type.

### 1.4 The rules, and the polarity that makes them galilean's own

The typed instance built here (`Test_GalLayerCensus.fs`, §1) implements eight
rule families beside `DisciplineKit.structuralArm`. Three of them are the exact
inverse of equiv's, which is why the kit's boundary is where it is:

| rule | equiv | galilean |
|---|---|---|
| `Cov + Cov` | legal (linear action) | **reject** — doubles U₀ |
| `−Cov` (negate) | legal (−I commutes with D) | **reject** — flips U₀ to −1 |
| component read of `Cov` | **reject** (basis-dependent) | **legal → Cov** (per-component, index-stable) |
| aggregate of `Cov`s | **reject** | **legal → Cov** if uniform |
| former conclusion guard | needs 4 inputs (type agreement, elementwise-linear test, source status) | needs **1** — the kernel body's status, verbatim |

**How much of the kit was usable, honestly [MEASURED].** The typed instance
consumes `DisciplineKit.structuralArm` unchanged for every arm it owns: vars,
if, match, let, block, sequence, assign, tuple-index, field, compute, lambda,
**the 104-line interprocedural call rule**, and the former-application walk. That
is the whole structural half, and it was reused without a single galilean-shaped
edit. The kit is real.

**Two places it did not fit, both recorded in the harness [MEASURED]:**

1. **`StructRules.CovAppliedAsCallee` receives only the callee's status.**
   Galilean's component-read rule is "…provided the indices are boost-invariant"
   (`MLGalilean.fs:401-406`); the proviso is not expressible through that hook.
   It costs nothing here because at typecheck `u(i)` is a `TExprIndex` — on the
   rules side, where the guard *is* applied — but a future port must not assume
   the hook is sufficient.
2. **`CallSig.CReturn` is a fixed status.** There is no shape in the kit's call
   signature for "the return's status is a function of the arguments' statuses",
   which is exactly what a *preserving* claim needs. See §6.

---

## 2. The instrument, and its calibration

`Blade.ML.Elaborate.expand` runs **before** `checkProgram`, so a seam BL4009
makes `typeCheck` return `Error` and the typed walker never sees the program.
**[MEASURED]**, obligation 1: on all 11 galilean-channel reject files, no
certificate reaches typecheck at all.

The way through is R1's, reused in method: rewrite `ml.galilean(` to a
test-registered inert conjunct. `MLGalilean.buildCertTable` then returns an EMPTY
table, which `MLElaborate.expandModule` short-circuits
(`if Map.isEmpty gcerts then []`), so the seam falls silent and the program
reaches typecheck still carrying the velocity names.

**The calibration differs from R1's, necessarily.** R1 calibrated against a LIVE
typed census; galilean has no production typed site to compare against. The
calibration used instead is the one that actually validates the instrument:
**on every file the seam accepts, the typed verdicts computed from the SHADOWED
source must equal those computed from the UNSHADOWED source, function for
function.** **[MEASURED]**, obligation 3: 14 accepted files, all agree. If
shadowing changed what the typed side sees, every rejection number would be
meaningless.

**Caveat [ARGUED].** As with R1, the calibration covers only programs the seam
accepts. The pipeline and the code path are the same on the reject probes, so
the risk is low but not zero.

**One instrument artifact, isolated and measured — see §4, family D.**

---

## 3. The acceptance census

**[MEASURED]**, `blade test gal-layer`. 23 certificates over 14 files.

| | certs | confirm | abstain | disagree |
|---|---|---|---|---|
| source-written | 17 | **16** | 1 | 0 |
| `SgsElaborate` stamps | 6 | 3 | 3 | 0 |
| **total** | **23** | **19** | **4** | **0** |

All four abstentions carry one reason: `walker declined (outside the galilean
fragment)`. Naming them:

| what | count | why |
|---|---|---|
| the generated `sgs.stress` body | 3 | its packed cell is `prodsum(u_a,u_b)/T − mu_a*mu_b` — a product of two boost-variant values, which the lattice refuses. The stamp is a genuine AXIOM: the cancellation argument is not a composition argument. |
| `filtered_shear` (sgs/013) | 1 | the `box_filter` blocker. §6. |

**The generated `sgs.grad` body CONFIRMS, in all three files where it appears
[MEASURED].** That is a positive result worth stating plainly: at the seam
`sgs.grad` is an axiom matched by NAME; at typecheck its synthesized body is
*re-proved* by the walker from its own stencil. Two of the three sgs axioms
(`grad`, `stress`) become table lookups keyed by binder id at typecheck, which is
strictly better than name matching — and one of the two is additionally
re-derived rather than trusted.

**Zero disagreements over 23 certificates [MEASURED].** The typed walker and the
seam checker do not contradict each other anywhere in the corpus. For equiv the
same figure required a 445-line walker plus a polynomial engine; galilean reaches
it with the kit's structural arms plus eight rule families.

---

## 4. The rejection census — the number that decides the layer

**[MEASURED]**, 11 galilean-channel reject files, 11 offender verdicts (the
functions the seam NAMED, recovered from its `function '<name>'` message prefix).

| Family | Files | Typed verdict | Would a flip still refuse? |
|---|---|---|---|
| **A** — the body is definitely boost-VARIANT | 1 | **DISAGREE** | **yes** |
| **B** — a rule refused, and the cause is discarded | 9 | ABSTAIN (`GBottom`) | **no** |
| **D** — the conjunct's SHAPE is wrong | 1 | (instrument artifact — see below) | **yes, free** |
| | **11** | | **2 yes / 9 no** |

### Family A — the one survivor (1 file)

`030_galilean_var_return_rejected.blade`: `function bad(uc, du) where
ml.galilean(uc) = uc + du`. `BVar + BInv → BVar`, and v1 certifies boost-INVARIANT
results only. The typed walker derives `GVar` for the body against a certificate
that asserts fixed, which is a definite contradiction, so it DISAGREES.

**[ARGUED]** This is the only rejection shape galilean's typed lattice can
express today, and it is expressible for the same reason equiv's family A is:
the walker reaches a *definite* status that contradicts the declaration. Every
other rejection is a `GBottom`, and `GBottom` says only "I decline".

### Family B — nine files, all lost (9 files)

`027` (sum), `028` (scale), `029` (escape to `sqrt`), `081` (`u * dt` under a
declared pin), `083` (unclassifiable argument), `sgs/014` (box_filter return),
and `diagnostics/019`, `020`, `043`. **[MEASURED]** — every one abstains with
`walker declined (outside the galilean fragment)`.

**This is the family the whole flip turns on [ARGUED], and it is the same
structural fact R1 found for equiv.** `MLGalilean` builds **33 distinct BL4009
messages inside the judgment** (plus 1 for the return-status refusal and 3 for
conjunct shape — 37 total) **[MEASURED]**, each with the offending
sub-expression's span. The typed lattice's refusal value is `GBottom`: a nullary
constructor with no cause and no span. The analysis has *already decided* the
body is not boost-invariant at these sites; it is the lattice value that throws
the reason away.

The messages lost here are the domain-teaching ones — *"adding two boost-variant
values doubles the U0-coefficient — subtract them (differences are
boost-invariant) or average through sgs.box_filter"*, *"a boost-variant value
escapes to 'sqrt', which carries no galilean certificate"*, and the deliberate
wording split of `083` between a genuine escape and the walker's own blind spot.

### Family D — an instrument artifact, and a free win (1 file)

`031_galilean_nonparam_rejected.blade` (`where ml.galilean(zz)`) reads as
`abstain` in the census, but **that is the shadow rewrite's doing, not a
finding**: the shadow conjunct's `Validate` is a no-op by construction.

**[MEASURED]** by probe (c). `MLGalilean.galileanHandler.Validate` is invoked at
typecheck by `TypeCheck.checkFunctionDecl` through the `Blade.Constraints`
registry (`TypeCheck.fs:9602`), and it re-checks exactly the two conditions
`buildCertTable` errors on. It cannot normally be observed because the seam wins
the race — unless the seam does not run at all. `MLElaborate.expandModule`
short-circuits with no `import ml` while `expandStr` still registers the
conjunct, so writing the normalized name directly in a module that does not
import `ml` reaches the handler and nothing else:

```
function bad(u: Float, v: Float) where __ml_galilean(zz) -> Float = u - v
  →  error[BL3999]: function 'bad': galilean argument 'zz' is not a parameter
                    of this function
function ok(u: Float, v: Float)  where __ml_galilean(u)  -> Float = u - v   → OK
```

The **sentence is byte-identical** to the seam's; only the code differs (BL3999
`Other` versus BL4009). **[ARGUED]** So one of galilean's six rejection families
is *already* typecheck-resident and survives a flip for free, needing nothing but
a code assignment. Nothing equivalent exists on the equiv side, because equiv's
conjunct handler validates only the group name.

---

## 5. The deduction differential — the strong positive result

**[MEASURED]** The harness builds a typed DEDUCTION twin —
`MLGalilean.inferGalileanCertificates` transplanted onto the typed AST: decl
order, one merged speculative table, every passing singleton proposed, the full
occurring set tried once when no singleton passes — and compares it to the
seam's live BL4014 channel over every accepted file that imports `ml`.

| | matched | seam-only (recall loss) | typed-only (false proposals) |
|---|---|---|---|
| typed deduction | **7** | **0** | **0** |

Per file:

| file | seam | typed |
|---|---|---|
| 078 stepper twin | `shear_loose(u)` | `shear_loose(u)` |
| 079 velocity difference | `slip_energy(uc, ub)` | `slip_energy(uc, ub)` |
| 082 dependency closure | `cell_diff(u)`, `pair_rate(u)` | same |
| 084 independent singletons | `flux_pair(u)`, `flux_pair(v)` | same |
| sgs/015 burgers LES | `smag_g(ub)` | `smag_g(ub)` |
| 080 vacuity silence | (silent) | (silent) |

**Exact reproduction, both directions [MEASURED].** This includes the three
behaviours the corpus pins as *silences*: vacuity (080's `drag_stub`, whose
velocity parameter is never read), failed-judgment silence (080's `advect_stub`),
and the several-passing-candidates non-threading (084's `total_flux`). It also
includes the dependency threading (082's `pair_rate` resolving against
`cell_diff`'s just-inferred summary) and the full-set candidate (079).

Compare equiv, where the B3 differential is `typed ⊇ seam` with a documented
extra-recall gap. **Galilean's deduction transplants with no residue at all.**

**[ARGUED] Why it transplants so cleanly.** Galilean's hypothesis space is
subsets of the PARAMETER NAMES, which are identical at both layers. Equiv's is
`candidatesFor` over the index-type FAMILIES appearing in the signature — a type
read that only exists in exact form at typecheck, which is precisely why equiv's
deduction *gains* from the move. Galilean's neither gains nor loses. It simply
works.

### 5.1 The one thing that does NOT transplant, and it is a real cost

**[MEASURED]** The seam's vacuity guard is `MLCertShell.freeVars` — an
**exhaustive** free-variable scan over the surface AST. **The typed AST has no
such function.** The nearest thing, `DisciplineKit.mentionsAnyId`, is
conservative in the WRONG direction for this use: its header states that an
unenumerated node kind answers TRUE, and `TExprBlock` is unenumerated.

**Measured consequence:** with `mentionsAnyId` as the guard, the typed deduction
proposes nothing at all on any block-bodied function — it reads every such
function as self-referential — and loses **3 of the 7** seam proposals (078,
079, sgs/015; the four survivors are the expression-bodied functions of 082 and
084).

`TypedExprKind` has **71 constructors** **[MEASURED]**, so writing the exhaustive
typed twin of `freeVars` is real work, not a line.

**The workaround the harness uses instead, and it is a genuine finding
[MEASURED]:** the guard is expressible through the walker itself. **Poison the
binding** — bind the candidate parameter to `GBottom` and every other parameter
to fixed, then walk. `GBottom` is absorbing in every arm of the kit and of the
rules, so a body that reads the parameter cannot come back with anything else,
and a body that never names it is unaffected. With that guard the differential is
**7/0/0**. Its two error modes both land on the safe side (a body that bottoms
for an unrelated reason wastes an attempt that then fails; a parameter read only
inside an unwalked lambda skips a candidate).

**One divergence the move creates and the seam does not have [MEASURED]:** by
typecheck the module also holds the compiler's own synthesized decls (`__sgs_N`,
`__ml_N`), which the seam never sees. A typed deduction must filter them or it
will propose pins on generated code. The harness filters by name prefix; a real
port would want a provenance flag.

---

## 6. The `box_filter` blocker — verified end to end, and scoped

### 6.1 Verified

**[MEASURED]**, in four independent steps.

1. **The stamping asymmetry is real.** `SgsElaborate.elabOp` emits all three sgs
   ops as generated `__sgs_N` function decls, and applies `galileanStamp ["u"]`
   to exactly two of them: `grad` (`SgsElaborate.fs:262`) and `stress`
   (`fs:281`). `box_filter` (`fs:268`) is emitted unstamped, with a 7-line
   comment (`fs:219-225`) explaining that its rule is status-PRESERVING and that
   `__ml_galilean` asserts boost-INVARIANCE, so stamping it would be a *false*
   axiom rather than a weaker one.
2. **The seam accepts the preserving use.** Probe: `let f = sgs.box_filter(u, 2)
   in f(...) - f(...)` under `where ml.galilean(u)` → `OK`. The corpus pins the
   same shape as `filtered_shear` in `sgs/013_galilean_axioms.blade`.
3. **At typecheck it declines.** `filtered_shear` is one of only two
   source-written certificates in the corpus that the typed walker does not
   confirm, and the only one that is not an sgs stamp. Verdict: **ABSTAIN**,
   reason `walker declined (outside the galilean fragment)`. The mechanism is
   exactly the one predicted: `box_filter` has become an *uncertified callee*
   `__sgs_N(u)`, and the kit's all-fixed rule declines on a moving argument.
4. **It cuts both ways.** `sgs/014_galilean_filter_return_rejected.blade` — the
   reject-probe that asserts a filtered velocity may not be RETURNED — also
   degrades from a precise BL4009 to an abstain, i.e. that program would
   silently compile after a flip.

The harness asserts the identification rather than assuming it: every generated
sgs former carrying no galilean stamp has arity 1, the `box_filter` shape
(obligation 0). If a fourth former is ever added with a different shape, that
assertion goes red.

### 6.2 Scoped: what a "preserves" claim costs

The harness prototypes one. **[MEASURED]** results:

| | acceptance | rejection |
|---|---|---|
| baseline | 19 confirm / 4 abstain | 1 of 11 offenders refused |
| with "preserves" | **20 confirm / 3 abstain** | **2 of 11 offenders refused** |

Specifically: `filtered_shear` goes `abstain → confirm`, and `sgs/014`'s `bad`
goes `abstain → disagree`. **The claim recovers the accept AND the reject.**

**The engine-side cost is nine lines** — one arm, placed before the kit's call
rule, that judges the arguments, requires every non-carrier argument to be fixed,
and returns the carrier's status. The data it needs is one integer: *which
parameter's status the result inherits.*

**Why it needs an arm of its own rather than a field [MEASURED]:** the kit's
`CallSig.CReturn` is a fixed status. There is no shape in that record for "the
return's status is a function of the arguments' statuses". A preserving claim is
a *dependent* signature, and the kit's call rule is not.

**Is it a small addition or a real design problem? [ARGUED] — Small, and the
decision that galilean owns its own vocabulary is what makes it small.**

The reason `SgsElaborate.fs:219-225` calls it "a discipline change, not a
stamping change" is that a claim vocabulary shared across disciplines cannot add
a concept unilaterally: equiv has status-preserving operations too (negation,
whole-array negate), and `design-discipline-as-data.md` §7 q2 flags the
vocabulary question as cross-cutting for exactly that reason. **That
consideration is now void.** Galilean's vocabulary is galilean's. The addition
is:

- one conjunct spelling, e.g. `ml.galilean_preserves(u)`, registered beside
  `__ml_galilean` with a `Validate` that is the existing one verbatim;
- one arm in whichever walker owns the rule (nine lines, measured);
- one stamping site in `SgsElaborate` (`fs:268`, one call to a second stamp
  helper);
- at the SEAM, `MLGalilean.judgeApp`'s `box_filter` arm already implements the
  rule (`fs:367-371`), so the seam needs only to consult the new conjunct for
  *user-written* preserving functions.

**The one genuine design question it raises [ARGUED]:** a preserving certificate
weakens the invariant the discipline guarantees. `where ml.galilean(u)` today
means "the result is the same in every frame". `galilean_preserves(u)` means "the
result shifts exactly as `u` does", which is a *different theorem* and must not
be usable where the first is expected. The lattice already distinguishes them
(`GVar` versus `GInv`), and v1's rule that a certified body must RETURN fixed is
exactly the guard that keeps them apart. So the risk is a vocabulary-design risk
(two pins that read alike and mean differently), not a soundness one. It is owed
a naming review, not a redesign.

**A note on ordering.** This fix is worth making **whether or not anything
moves**. It costs the seam nothing (the rule is already written there) and it
buys user-written preserving functions a spelling they do not have. The layer
decision does not gate it.

---

## 7. Message parity

**[MEASURED]** `tests/corpus/diagnostics` pins BL4009 in **3 files**, carrying
**3 `ERROR-CONTAINS` substrings** and **2 span pins** (`019 @ 5:3`, `020 @ 5:8`;
`043` pins the code without a column).

**All 3 substrings and both span pins would die under a flip.** The typed side
produces no text at all at these sites — its answer is `walker declined
(outside the galilean fragment)` — and `GBottom` threads no span.

`tests/corpus/ml-equiv` and `tests/corpus/sgs` pin **no** BL4009 message text:
their reject-probes assert only the `(rejects)` suffix. So the pin-rewriting cost
of a flip is 3 substrings and 2 spans — **an order of magnitude smaller than
equiv's 37 and 13**, because galilean has no engine and therefore no long-form
engine messages to port.

**[ARGUED]** The cost is small in PINS and large in MESSAGES. Only 3 pins would
need rewriting, but 33 in-judgment BL4009 messages would need *writing*, because
the typed side has none. Nine of the eleven corpus rejections turn on exactly
those messages.

---

## 8. Recommendation

### 8.1 Deduction → typecheck. Recommended, and cheap.

**[ARGUED], on the measurement of §5.**

- The differential is **7 matched / 0 lost / 0 false**, exact in both directions,
  including all three pinned silences. No other discipline has reached that.
- Galilean's hypothesis space and classifier are layer-independent (§1.2, §1.3),
  so the port carries no classification risk at all.
- The one real cost is the missing exhaustive typed `freeVars` (§5.1), and the
  poison-probe guard closes it without writing one.
- The one new divergence is synthesized decls entering the candidate set (§5.1),
  which needs a provenance filter.

The gate this should ship behind is the shape `Test_RepDifferential` uses and
this block already implements: **typed proposals ⊇ seam proposals over the whole
corpus, and zero proposals the seam does not make** (obligations 5 and 6 here).

**Note it can move without moving checking**, and should: the deduction channel
is warnings-only, so a typed deduction that regressed would cost a suggestion,
never a compile.

### 8.2 Checking → stays at the seam. Recommended, but not for equiv's reason.

**[ARGUED], on §4 and §7.**

Equiv's checking stays because its *evidence* is gone by typecheck — the `ml`
vocabulary has been rewritten into generated calls, and R1 measures 6 of 30
rejections lost to exactly that. **That reason does not apply to galilean at
all.** Galilean's evidence survives: the typed walker judges the same 23
certificates the seam judges and agrees with it on every one (§3). If evidence
were the criterion, galilean could flip wholesale.

Galilean's reason is different and narrower: **the lattice value `GBottom`
carries neither a cause nor a span, and 9 of 11 corpus rejections are `GBottom`.**
A flip today makes those nine programs compile silently. That is not a
capability problem; it is a diagnostics problem, and it is the *cheaper* of the
two problems R1 identified for equiv (galilean has no engine, so there is no
second family of long-form messages to port; §7).

**So the honest form of this recommendation is conditional, and that is the
notable difference from equiv:**

> **Galilean checking can flip wholesale — equiv cannot — once `GBottom` carries
> a cause and a span, plus provenance on ONE family. Nothing else stands in the
> way.**

Concretely, the remaining work:

| family | corpus files | closed by |
|---|---|---|
| A (definite variant) | 1 | already works |
| D (conjunct shape) | 1 | already works, at typecheck, today (§4) — needs only a code |
| B (rule refused) | 9 | giving `GBottom` a cause + span, at 33 sites |
| C (`ml.*` op vocabulary gone) | 0 (probe (d)) | the above, PLUS provenance in the elaborator stamp — R1's stage C3-2 |

That is **three** items against equiv's six families, and two of the three are
already done or done-by-construction.

**[MEASURED]** by the census: every galilean rejection *in the corpus* lands in
A, B, or D. There is no family E (semantic divergence) and no family F (refused
by a later stage anyway) — two of the three families that make equiv's flip a
*hybrid* rather than a flip.

**Family C exists, and the corpus does not defend it.** Equiv's family C is "the
`ml` vocabulary is gone by typecheck", and galilean has a rule in that family:
`MLGalilean.judgeApp:377-380` refuses a boost-variant argument to any surface
`ml.*` op. **No corpus reject-probe exercises it**, so this census wrote one
(probe (d) in the harness) rather than leave the family unmeasured:

```blade
function bad(u: Array<Float like Idx<3>>) where ml.galilean(u) -> Float = {
    let sh = ml.y_to(1, u(0), u(1), u(2))     // BVar into an ml.* op
    ...
}
```

**[MEASURED]** seam: BL4009, *"ml.y_to does not accept boost-variant
arguments…"*, at the call's span. Typed: **ABSTAIN**.

**[ARGUED]** and this is the one place galilean's picture is genuinely worse
than §8.2's headline. The typed side does *decline* here — by typecheck
`ml.y_to(…)` is a call to a generated `__ml_N` with moving arguments, which the
kit's all-fixed rule refuses — so giving `GBottom` a cause WOULD restore a
refusal. But the message would name `__ml_1`, not `ml.y_to`. That is exactly
R1's family C and it needs exactly R1's fix: extending the A1 elaborator stamp
with the originating op name and span. One extra family, and it is the only one
here that is not closed by the cause-and-span change alone.

**A corpus gap worth closing regardless [ARGUED].** That probe should become a
corpus file. The rule is live in the shipped checker and nothing pins it.

### 8.3 Are they the same answer? No — and that is the finding.

For equiv, "deduction typed, checking at the seam" is a *split* forced by two
different obstacles (types are at typecheck; vocabulary is at the seam). For
galilean it is a **staging decision about one obstacle**: deduction can move
today because it needs nothing; checking can move as soon as the refusal value
learns to explain itself. They are the same shape and not the same reason, and
galilean's checking is genuinely closer to movable than equiv's.

### 8.4 Ordering, if any of this is scheduled

1. **The `preserves` claim + the `box_filter` stamp** (§6.2). Independent of the
   layer question, cheap, and it removes the one measured recall loss on the
   accept side and one on the reject side. Do it first because it is the only
   item here that is a *capability* gain rather than a plumbing move.
2. **Typed deduction** (§8.1), behind the 7/0/0 differential. Warnings-only,
   so low risk.
3. **`GBottom` gains a cause and a span** — 33 sites. This is the whole cost of
   making galilean's checking movable, and it is worth doing on its own merits
   (it is also what would let the seam and a typed second opinion agree on
   *wording*, which is the drift risk `MLPerm`'s header documents).
4. **Provenance in the elaborator stamp** for family C — R1's stage C3-2,
   shared with equiv and worth doing once for both. Also: turn probe (d) into a
   corpus file, since the `ml.*`-op rule is live and unpinned today.
5. **Then, and only then**, the checking flip, gated on the census in §4 turning
   9 abstains into 9 disagrees, on family C naming `ml.y_to` rather than
   `__ml_1`, and on the 3 diagnostic pins being re-derived.

---

## 9. Reproducing

```
dotnet build Blade.fsproj -c Release
dotnet bin/Release/net7.0/Blade.dll test gal-layer
```

The block prints, per file: the seam's channel and first diagnostic, the typed
verdict for every galilean certificate (with `*` marking the seam's named
offenders and `~`/`*` marking generated decls), the deduction differential under
both vacuity guards, and the `preserves` deltas. It asserts seven obligations:

0. every unstamped generated sgs former has arity 1 (the `box_filter`
   identification);
1. no galilean-rejected file reaches typecheck with any certificate;
2. no typed CONFIRM on a function the seam named as a galilean offender;
3. the shadow calibration;
4. non-vacuity;
5. typed deduction loses no seam proposal;
6. typed deduction proposes nothing the seam does not.

Everything else is a `[SKIP]` census line, so a corpus change moves the numbers
without turning the block red for the wrong reason.

**Residue.** The block registers a constraint handler named
`__gal_layer_census_shadow` in the process-wide `Blade.Constraints` registry,
which has no unregister. Safe — no test pins the registered-vocabulary list, and
the spelling is unwriteable by accident. (This is the same residue
`Test_RepRejectCensus` leaves.)

**The instrument is not a discipline.** `tests/Test_GalLayerCensus.fs` §1 is
experimental and must stay in the test assembly. If a decision is taken on the
strength of these numbers, promote it deliberately with its own gate, or delete
it.
