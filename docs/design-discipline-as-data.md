# Design: three disciplines, one shared library — the C3 characterization

Status: **STAGE 0 LANDED (2026-07-29); PHASE REFRAMED.** `src/DisciplineKit.fs`
is real code and `DeduceRep` is its first caller; both gates diff
byte-identical against the frozen baseline (§7). Equiv is **DONE** (§6).
Galilean and perm are open and may proceed independently and in parallel (§8).

This document answered the stage-C3 question in
`plan-equivariance-in-types.md` §2/§3: *"A DISCIPLINE is data … One generic
engine in Deduce.fs runs them all."* The measurement said no, and the phase has
since been reframed to match.

> **User decision, 2026-07-29:** *"Let's start by relaxing the requirement to
> house all equivariances under one abstraction. Each equivariance has its own
> `ml.equiv`."*

So: **three disciplines, three claim vocabularies, no unified engine.** That is
what §3.2's polarity table argued, what §9 q2 recommended, and what
`MLCertShell.fs` concluded at the elaboration seam before any of this started.
The unified-engine premise was the plan's, not the code's.

Claims are marked **MEASURED** (verified by reading the cited source, by
building, or by a named test block) or **ARGUED** (a judgment from the
evidence).

---

## 0. The verdict

**The walker abstracts; the rules do not; and the right shape for the shared
part is a LIBRARY, not a framework.**

1. **The WALKER abstracts, and it was worth extracting.** 148 code lines of
   `DisciplineKit.structuralArm` — including the interprocedural call rule, the
   longest and most soundness-critical arm in the walker — mention no
   discipline payload. Stage 0 extracted them and proved the extraction inert.
   (MEASURED: §7.)

2. **The RULES do not abstract, and must not.** The three disciplines have
   *opposite polarity* at nearly every arithmetic arm, because the three
   actions are different algebraic structures: equiv's is a **linear**
   block-diagonal rep, galilean's an **affine shift**, perm's a **permutation
   matrix**. `Cov + Cov` is legal for equiv, a *reject* for galilean, legal for
   perm. Negation *preserves* for equiv and perm and *rejects* for galilean. No
   parameterized payload recovers this. (MEASURED: §3.2, every cell cited.)

3. **Therefore the kit is a library.** A discipline MAY call it for the generic
   walk and the shared guards. It is not required to instantiate anything, and
   one whose walk genuinely differs is free to write its own and call only the
   helpers. §5 says which parts are worth calling and which to bypass.

Two corrections to the original plan, both accepted and folded in upstream:

- **§2's "Classifier: IRType -> status" is impossible for galilean**, whose
  boost-variance is deliberately not a type property. (MEASURED: §4.2.)
- **§0.2's mechanism for unblocking perm inference is wrong.** "Monomorphized
  extents make N concrete" does not hold — `16 = 2⁴ = 4²`, and the seam already
  resolves extents statically. The real unblock is a parameterized index tag
  propagated nominally by the unifier, which is a type-system addition.
  (MEASURED: §4.4.)

And the negative result that shaped galilean's track: **moving galilean to
typecheck loses the `sgs.box_filter` rule** (§4.3).

---

## 1. What already existed, and what it already decided

This abstraction had been attempted once, at the seam, and the attempt
deliberately stopped short of the rules. `src/ml/compiler/MLCertShell.fs` was
extracted at the third witness (`MLCertShell.fs:1-36`):

> WHAT DOES NOT LIVE HERE is every RULE: the status lattices, the signature
> classifiers, the judgment arms, the op tables, the diagnostics. The three
> lattices have OPPOSITE POLARITY at several arms … Shell = the walk; module =
> the rules.

What it owns today: `judgeEach`, `patternVars`, `bindPatternVars`, `freeVars`,
`conjunctsOf`. Pure syntax. (MEASURED.)

**This design agrees with that finding.** Its argument against sharing more was
explicitly a *cost* argument — "six moving parts to share twenty-odd lines,
which is a worse trade than the copy". At the seam `judgeStmts` really is ~25
lines. At typecheck the shared surface is 148, including a 104-line call rule
that all three disciplines implement identically and that has **already drifted
between copies once**: MLPerm's stage-5c catalog found four divergences, two of
them false ACCEPTS — certificates issued for functions lacking the property.

The moral that catalog draws is the design principle here (MEASURED quote):

> the copies drift in the GUARDS, not in the rules. Every divergence the diff
> found was a place where one walker checked something the others did not —
> never a place where two walkers checked the same thing and disagreed about
> the answer. The polarity table below is the intended disagreement; a guard
> only one copy has is a bug in the other two until argued otherwise.

That sentence is why §5 exists: **guards are the part worth sharing**, and they
are the part a discipline should be reluctant to reimplement.

---

## 2. The five axes, three disciplines

### 2.1 Status lattice

| | equiv (`MLEquiv.fs:105-108`) | galilean (`MLGalilean.fs:52-55`) | perm (`MLPerm.fs:151-167`) |
|---|---|---|---|
| moving | `Rep of RepSpec` | `BVar` | `Pow of int` (k ≥ 1) |
| fixed | `Inv of InvShape` | `BInv` | `Pow 0` |
| fixed, weak | `Inv InvShapeUnknown` | *(none)* | `PowUnsized` |
| unclassifiable | `Opaque` | `BOpaque` | `POpaque` |
| decline | `Error` (typed: `TBottom`) | `Error` | `Error` |

```fsharp
// MLEquiv.fs:82-108
type RepSpec = O3Spec of Spec | PgSpec of string * (string * int) list
type InvShape = InvScalar | InvAgg of rank: int option | InvShapeUnknown
type RepStatus = Rep of RepSpec | Inv of InvShape | Opaque

// MLGalilean.fs:52-55
type BoostStatus = BVar | BInv | BOpaque

// MLPerm.fs:151-167
type PowStatus = Pow of int | PowUnsized | POpaque
```

**The lattice abstracts cleanly in SHAPE** — `SCov of 'Cov | SFix of 'Fix |
SOpaque | SBottom`, with `'Cov`/`'Fix` = `RepSpecT`/`InvShapeT`, `unit`/`unit`,
and `int`/`Sized|Unsized` respectively. Two notes on faithfulness:

- **Perm's re-encoding is mechanical and sound.** MLPerm spells the invariant as
  `Pow 0`, sharing a constructor between the moving and fixed roles. Every arm
  matching `Pow 0` means *invariant* (`MLPerm.fs:586, 593, 602, 660`); every arm
  matching `Pow k when k > 0` means *covariant* (`MLPerm.fs:492, 549, 557,
  621`). The split is already in the source. (MEASURED.)
- **Galilean's empty payload is a real slot, not an absence.** `BVar` tracks
  "U0-coefficient EXACTLY 1" (`MLGalilean.fs:22`) and its named v2 is "rational
  U0-coefficient tracking" (`:34-35`) — precisely a payload. Today `unit`.

### 2.2 Classifier

| | classifies from | fully-annotated gate | ambiguous? |
|---|---|---|---|
| equiv | index-type family (`TyIrrepsIdx` / `TyPgIrrepsIdx`) | YES (`MLEquiv.fs:430-431`) | no |
| galilean | **nothing typed** — the conjunct's parameter NAME list (`MLGalilean.fs:103-107`) | NO | no |
| perm | flat extent arithmetic, `powClass N M` (`MLPerm.fs:239-260`) | YES (`:328-329`) | **YES** |

### 2.3 Transfer table

See §3.2 — the axis where the disciplines irreducibly differ.

### 2.4 Claim vocabulary

Uniform in MECHANISM, and now deliberately distinct in MEANING. All three are
`WhereClause.Custom` entries normalized to `__ml_equiv` / `__ml_galilean` /
`__ml_perm_equiv` and read by the shared `MLCertShell.conjunctsOf`. (MEASURED.)

Note `Ast.Constraint.CnEquiv` exists but **has no constructor site** and is
documentation only (`Ast.fs:288-300`). Do not build on it.

| | suggestion channel | code | structured fact |
|---|---|---|---|
| equiv | `Equiv.CertSuggestions` (`MLEquiv.fs:1537-1549`) | BL4011 | `CertFacts`, kind `"equiv"` |
| galilean | `Galilean.GalCertSuggestions` (`MLGalilean.fs:117-122`) | BL4014 | `CertFacts`, kind `"galilean"` |
| perm | **none** | BL4012 is an *error* code | **none** |

`CertFacts` is hosted *in MLEquiv* purely for F# compile order (MLEquiv 114 <
MLGalilean 115 < Ide 159), and MLGalilean writes into it. A perm channel would
face the same placement question. (MEASURED.)

### 2.5 Mode

| | gate | hypothesis space | selection |
|---|---|---|---|
| equiv | `import ml` **and** `candidatesFor ≠ []` | `[O3; SO3]` or `[Point g]` | **first passer** |
| galilean | `import ml` only — no signature gate | param subsets: every free-occurring singleton, else the full occurring set once | **every passer** |
| perm | `import ml` **and** a non-empty cert table | *none* — no inference exists | n/a |

Two corrections to how the plan described this:

- **"Galilean's gateless sweep" is gateless only below the import gate.**
  `MLElaborate.expandModule` short-circuits the whole pass without an `import
  ml` alias (`MLElaborate.fs:2007-2013`). Galilean's distinction is that it has
  no *signature-family* gate. (MEASURED.)
- **Selection differs for a principled reason.** Equiv's candidates are
  competing strengths; galilean's are independent claims — `galilean(u)` and
  `galilean(v)` are both true and suppressing either hides a theorem
  (`MLGalilean.fs:477-480`). (MEASURED quote; ARGUED conclusion.)

---

## 3. The evidence

### 3.1 The walker's structural/rule split

`DeduceRep.statusOf` had 28 top-level arms. Sorted by whether they mention a
discipline payload: 16 structural (~250 line-range lines, including the
104-line `TExprApp`, 34-line `TExprApply`, 35-line `TExprBlock`) and 8 rule
families (~100).

**Post-stage-0 recount in CODE lines** (comments and blanks excluded), which is
the honest number and less flattering than the line-range one:

| | code lines |
|---|---|
| `DisciplineKit.structuralArm` — generic, available to every discipline | **148** |
| equiv's `ruleArm` — its own rules | 62 |
| equiv's kit glue (`StatusOps` + `StructRules` + `WalkCtx` + the knot) | 48 |

**For ONE discipline this is roughly net-neutral** — 148 + 48 against the ~210
it replaced. The abstraction starts paying at the SECOND caller, which pays ~48
and gets 148 free. Stating that plainly matters: stage 0's deliverable is not
smaller code, it is **one** copy of the call rule instead of the three that
have already drifted once. (MEASURED.)

### 3.2 The polarity table

MLPerm's header (`MLPerm.fs:61-75`) tabulates equiv vs perm; reading MLGalilean
beside them completes it.

| rule | equiv | galilean | perm |
|---|---|---|---|
| `Cov + Cov` (same payload) | **legal** → Cov | **REJECT** — "doubles the U0-coefficient" <br/>`MLGalilean.fs:184-185` | **legal** → Pow k <br/>`MLPerm.fs:581-582` |
| `Cov − Cov` | legal → Cov | **legal → FIXED** — "THE rule: the boost cancels" <br/>`MLGalilean.fs:183` | legal → Pow k |
| `−Cov` (negate) | **legal**, preserves (−I commutes with every D) | **REJECT** — "flips its U0-coefficient to −1" <br/>`MLGalilean.fs:172` | **legal**, preserves <br/>`MLPerm.fs:512-514` |
| scalar · Cov | legal *iff provably scalar* | **REJECT** <br/>`MLGalilean.fs:192-193` | legal (broadcast) |
| `Cov · Cov` | **REJECT** — CG's job | REJECT | **legal** → Pow k <br/>`MLPerm.fs:575-582` |
| pointwise nonlinearity on Cov | **REJECT** | REJECT | **legal** <br/>`MLPerm.fs:64` |
| component read of Cov | **REJECT**, except a static offset in a trivial block | **legal → Cov** — "per-component and index-stable" <br/>`MLGalilean.fs:401-406` | REJECT in v1 (legal in the maths) <br/>`MLPerm.fs:111-121` |
| aggregate of Covs | **REJECT** | **legal → Cov** if uniform <br/>`MLGalilean.fs:152-161` | REJECT + extent test <br/>`MLPerm.fs:483-499` |

**Why** (ARGUED, premises cited above):

- equiv's action is **linear** (`x ↦ D·x`). Linear maps commute with `+`, `−`
  and scalar `·`, so the moving set is a linear subspace. They do *not* commute
  with nonlinearities or products.
- galilean's is an **affine shift** (`u ↦ u + U₀`). Affine shifts do not commute
  with addition at all; the moving set is a *torsor*, and differences land in
  the fixed space. Hence the unique `Cov − Cov → Fix` rule and the rejection of
  `Cov + Cov`.
- perm's is a **permutation matrix** — monomial, 0/1. It moves cells without
  mixing them, so it commutes with *every* pointwise map. Hence products and
  nonlinearities are legal — exactly what equiv forbids (`MLPerm.fs:71-75`:
  "One judgment cannot wear both").

**This is the negative result, and the reframe follows from it.** Three
different algebraic structures, not three payloads.

---

## 4. What each discipline needs, one at a time

### 4.1 equiv — has what it needs; see §6 for its settled end state

`IrrepsIdx`/`PgIrrepsIdx` ride `IRIndexTypeG.Tag` through unification
(`Types.fs:157-228`, `Unify.fs:506-521`). The typed side is *stronger* than the
seam in one measured place: `IRTScalar` is provably 0-dimensional, where the
surface classifier had to guess from builtin names.

### 4.2 galilean — needs nothing from types

§2's `IRType -> status` classifier is impossible here:

```fsharp
// MLGalilean.buildCertTable — MLGalilean.fs:103-107
let ps = fd.Params |> List.map (fun p ->
             (p.Name, if List.contains p.Name args then BVar else BInv))
```

Boost status comes from the conjunct's argument list, positionally, and cannot
come from a type by design (`MLGalilean.fs:17-20`):

> Units are deliberately NOT the seed: a velocity DIFFERENCE still carries the
> velocity unit but is boost-invariant — units track dimension, not frame
> behavior. The conjunct names the boost-variant parameters.

Galilean gains from a move to typecheck only binder-IRId keying (shadowing
safety) and the desugaring handling. (MEASURED premise; ARGUED conclusion.)

### 4.3 galilean's blocker — `sgs.box_filter`

Three of galilean's rules are surface-visible sgs formers
(`MLGalilean.judgeApp:353-375`). By typecheck sgs has elaborated
(`TypeCheck.fs:10803` ml, then `10809` sgs) and those calls are loop nests.

Two of the three are already rescued: **SgsElaborate already stamps
`__ml_galilean`** onto what it synthesizes (`SgsElaborate.fs:227`). Plan A1
asked someone to verify sgs stencils *could* carry galilean stamps; they
already do, so `grad` and `stress` become certified callees — better than the
seam's name-matching.

`box_filter` is the designed exception (`SgsElaborate.fs:219-225`, MEASURED):

> WHY box_filter IS NOT STAMPED. It is the one former whose seam rule is
> STATUS-PRESERVING rather than invariant-producing … A `__ml_galilean`
> certificate asserts a boost-INVARIANT result, so stamping it would be a false
> axiom, not a weaker one. The v1 claim vocabulary has no spelling for
> "preserves"; giving it one is a discipline change, not a stamping change.

The emitter confirms it: all three become generated function decls, only two
stamped (`SgsElaborate.fs:262, 268, 281`):

```fsharp
| "grad", ...        -> ensure st ... (fun nm -> galileanStamp [ "u" ] (gradDecl nm n))
| "box_filter", ...  -> ensure st ... (fun nm -> boxFilterDecl nm n w)     // no stamp
| "stress", ...      -> ensure st ... (fun nm -> galileanStamp [ "u" ] (stressDecl nm n w))
```

So at the seam `box_filter(u, w)` with `u : BVar` yields `BVar`; at typecheck it
is an **uncertified callee** and a `BVar` argument **declines**. A real recall
regression, measurable, and fixing it means adding a *preserves* claim — which
the source itself classifies as a discipline change. Hence track G3 (§8).

### 4.4 perm — blocked on a type-system addition

**The plan's §0.2 mechanism does not hold.** Two questions are conflated:

1. *Is the extent M concrete?* The seam already resolves it with `evalExpr
   statics fuel extentE` (`MLPerm.fs:242-247`). Monomorphization helps only for
   size-polymorphic signatures — a narrow recall gain.
2. *Given M, what is N?* Not given for inference; it comes from the conjunct
   (`MLPerm.resolveN:285-301`). An `Idx<16>` axis reads as `Pow 4` at N=2,
   `Pow 2` at N=4, `Pow 1` at N=16. **Monomorphizing 16 does not disambiguate
   16.**

Question 2 is the blocker, and MLEquiv says so (`MLEquiv.fs:1605-1607`):
*"guessing N from an `Array<_ like Idx<n>>` would propose noise."* MLPerm's own
named fix is *"Nominal keying (`Nat<Node>`) is the named upgrade."*
(`MLPerm.fs:101-108`.)

**Typecheck offers that upgrade.** A named index type sets `Tag = Some name`
(`TypeCheck.fs:772, 9991, 10056`) and the unifier treats user-named tags
**nominatively** (`Unify.fs:519`, MEASURED):

> User-named tags are nominative: lat != lon even if both Idx<180>.

But names alone are not enough — perm keys on *flat* `N^k` axes, and nothing
relates `Idx<16>` to `Idx<4>`. What perm needs is its own **parameterized tag**,
minted as `mkIrrepsTag` is (`Types.fs:157-163`):

```
"__nodepow:<N>:<k>"
```

with an `IxKNodePow` kind beside `IxKIrreps`/`IxKPgIrreps`. Then classification
is a tag read, N comes out of the type, inference is unblocked, and the
coincidental-extent caveat disappears. The precedent is exact — `IxKPgIrreps`
made this move as a "twin, not reroute" (`Types.fs:113-117`) — but it touches
the surface language, so it is its own high-risk track (§8, P1).

---

## 5. The kit as a LIBRARY: what to call, what to bypass

The reframe's practical content. `src/DisciplineKit.fs` offers services; it
imposes nothing. A discipline that instantiates none of it is a legitimate
discipline.

### 5.1 The criterion, recorded in the kit's header

> A rule belongs in the kit IF AND ONLY IF ITS SOUNDNESS ARGUMENT QUANTIFIES
> OVER ANY ACTION — the justification never names what the group does to a
> value, only whether the value MOVES or is HELD FIXED.

Worked both ways: the call rule's all-fixed fall-through qualifies ("a
deterministic map of fixed inputs gives the same output in every frame" — no
representation, boost or permutation appears). `Cov + Cov` does not ("the action
is LINEAR" — true for equiv and perm, false for galilean).

### 5.2 The anti-drift core — call these

These are where the three seam walkers historically drifted, and **every
divergence the stage-5c diff found was a false ACCEPT**. A discipline
reimplementing one of these is re-opening a closed bug.

| service | the drift it closes |
|---|---|
| **the callee guard** in `structuralArm`'s `TExprApp` — an Opaque or Bottom callee declines | MLEquiv had no `OpApply` arm, so a former over a rep answered Opaque and a read out of an Opaque binding answered *Inv*. corpus ml-equiv/049 |
| **judging the former's SOURCES**, not just scanning names | MLPerm cleared a node-covariant array appearing only in a former's source list. corpus ml-equiv/045, 046 |
| **`ParamMatches` ≠ `Join`** — an Opaque argument must never satisfy a parameter | collapsing them is a silent false accept that both gates pass |
| **`mentionsAnyId` conservative-TRUE on unenumerated nodes** | the `freeVars` catch-all that made `method_for(x) <@> …` invisible |
| **the self-reference guard** (`Self`) | a summary proving itself |
| **element-write index judging** in the block fold | went unjudged in MLEquiv and MLPerm while MLGalilean folded over them. corpus ml-equiv/047, 048 |

Also worth calling, though not drift-related: `staticIntOf`,
`isElementwiseArith`, and the `Checking` flag's plumbing.

### 5.3 What to bypass freely

- **The whole rules half.** `ruleArm` is equiv's. Galilean and perm write their
  own; §3.2 is the proof they must.
- **`Status<'Cov,'Fix>`.** Offered, not required — equiv itself does not use it
  (§7.2).
- **`structuralArm` entire**, if a discipline's walk genuinely differs. The
  clearest live case: **perm v2's per-axis status vectors**
  (`MLPerm.fs:94-99`). One status per VALUE is a v1 limitation; per-axis vectors
  change the walk's *shape*, not just its verdicts, and a discipline that needs
  them should write its own walk and call only §5.2's helpers. Bending the kit
  to accommodate that would recreate the framework the reframe removed.
- **The `Discipline` record of §10.** It is a design for a genericized DRIVER
  and nothing has been built against it. Build it only if two disciplines turn
  out to want the same driver — and only then, against both.

---

## 6. Equiv's settled end state — DONE

Equiv is finished, and its answer is a **split by layer**, chosen on
measurement rather than architecture. **Neither half generalizes**: galilean and
perm must each be measured on their own terms (§8).

### 6.1 Equiv CHECKS at the seam, because that is where its evidence lives

R1's rejection-parity census (`docs/census-rejection-parity.md`, MEASURED)
found the deciding fact. Of 47 `(rejects)` probes in `tests/corpus/ml-equiv`,
30 are refused on the equiv channel, and **family C — 6 files (008, 035, 038,
087, 091, 105) — rejects an argument to a surface `ml.*` op that is simply gone
by typecheck**, rewritten into calls to generated `__ml_N` functions. The typed
walker abstains on all six with `nothing established for the body`.

That is **not a gap to close — it is a fact about the pipeline**, and it is the
same fact `plan-equivariance-in-types.md` §1 states as the reason the walkers
live at the seam. The census's proposed remedy (its stage C3-2: stamp the
originating op name and span forward) would work, but it would mean **the typed
checker judging a reconstruction of the surface program** — which dissolves the
architectural case for flipping in the first place. (Census MEASURED; the
inference is ARGUED, and it is the reason equiv stops here.)

The census's wider numbers point the same way: 16 of the 30 equiv rejections
would silently start compiling after a flip, and 35 of 37 worded BL4008 pins
would die, because the typed lattice's refusal value carries neither a cause nor
a span.

### 6.2 Equiv DEDUCES and VALIDATES at typecheck, because that is where the exact types live

Shipped and gated (phases B/C1/C2): partial-annotation recall from closed types,
monomorphized extents, and the polynomial engine as discharger. The C1 second
opinion runs beside the seam with no authority — CONFIRM/ABSTAIN/DISAGREE, with
disagreement a compiler-bug signal.

Both halves are gated by `blade test rep-differential` and `blade test
rep-check`, and stage 0 left both byte-identical (§7).

**This is the "B-forever" end state `plan-equivariance-in-types.md` §5.3 asked
about, reached by measurement.** Deduction typed, checking at the seam.

One follow-up remains open and is worth doing regardless: the census recommends
keeping a **seam-resident lint** for family F (006/009/047/052/074) so the
cross-group explanation survives — a note with no authority to refuse.

---

## 7. Stage 0, as landed

### 7.1 The gate

Both blocks diff **byte-identical** against the frozen baseline
(`c3-stage0-baseline.txt`):

```
Rep Deduction Differential: 126 passed, 0 failure(s), seam 16, typed 19,
                            16 matched, 0 exempt, 3 win(s)
Equiv Certificate Agreement (C1): 60 passed, 0 failure(s),
                            54 confirm, 76 abstain, 0 disagree
```

Blast radius: `Blade.fsproj`, `DeduceRep.fs`, `DisciplineKit.fs`. MLEquiv,
MLGalilean, MLPerm, TypeCheck, TypeEnv and MLPolyExtractTyped **untouched**.
(MEASURED.)

### 7.2 What was built — and the abstract-status decision

```fsharp
type StatusOps<'St>    = { Bottom; Opaque; FixTop; FixScalar
                           IsCov; IsFix; IsBottom; IsOpaque
                           Join; ParamMatches; FixOfType; ClassifyTy }
type StructRules<'St>  = { CovAppliedAsCallee; FormerConclusion }
type CallSig<'Hyp,'St> = { CHyp; CParams; CReturn }
type WalkCtx<'Hyp,'St> = { Ops; Rules; Hyp; HypEq
                           Certified; Speculative; Self; DepHits; Checking }
val structuralArm : WalkCtx<_,_> -> (Map<IRId,'St> -> TypedExpr -> 'St)
                        -> Map<IRId,'St> -> TypedExpr -> 'St option
```

**The status is ABSTRACT (`'St` + a 12-field operations record), not the kit's
own DU.** This was a safety decision, not a taste one, and it should not be
"simplified" later.

`RepStatusT` is a real F# discriminated union, and every match over it — in
DeduceRep, MLPolyExtractTyped and TypeCheck — is checked for EXHAUSTIVENESS by
the compiler. Making it an abbreviation of a generic DU would turn its
constructors into partial active patterns at ~200 call sites, which **silently
switches that checking off** — in exactly the 445-line walker whose entire risk
is a dropped arm, during a refactor whose entire value proposition is that it
provably changes nothing. **Zero `FS0025` warnings repo-wide after the refactor
is the receipt.** (MEASURED.)

The 12 fields are the honest price, and they are the same "moving parts"
objection MLCertShell raised. It was right at the seam and wrong here only
because the quantity changed: ~25 lines shared there, 148 here.

`ParamMatches` is deliberately **not** `Join >> Option.isSome` — see §5.2.

### 7.3 What stage 0 did NOT genericize

The **driver**. `classifySignature`, `candidatesFor`, `deduceFunctionRep` and
`checkDeclaredRep` remain equiv-specific, because nothing else needs them yet.
Genericizing a driver against one witness is how §2's classifier axis went
wrong. The `Discipline` record (§10) stays a design.

### 7.4 The must-survive checklist — verified

Six behaviours were identified as easy to drop silently while both gates stay
green. Each was verified in the post-refactor source:

| # | behaviour | verification |
|---|---|---|
| 1 | `Checking` demotes the certified-callee all-invariant fall-through to `TOpaque` in validation mode only | on `RepCtx`, threaded into `WalkCtx`, consumed by the kit's call rule; `true` at `checkDeclaredRep`, `false` at `deduceFunctionRep` |
| 2 | `LieGuardFailure` caught SPECIFICALLY → `EngineRefutes`; all other exceptions → `None` | untouched: `TypeCheck.fs:10794` catches it, `MLPolyExtractTyped.fs:626` reraises past its own `try/with` |
| 3 | engine hook: composition-declined only, never overrides, per-group inside the ladder, `None` = N/A, `RepParam` positional | `engineFallback` only on `TBottom`/`TOpaque`; `engineUpgradeOnly` honours a discharge but never a refutation; the deduction call still sits INSIDE `attempt` |
| 4 | self-reference asymmetry | `Self = Int32.MinValue` when checking (assume), `Self = funcId` when deducing (refuse); `recordCertified` still precedes `checkDeclaredRep` |
| 5 | narrow DISAGREE | only definite `TRep`-vs-`TRep` with different specs; all other mismatches abstain, all six reason strings intact |
| 6 | post-elaboration rules | `OpNeg`/`OpMath` split preserved and annotated with why it must not be collapsed; former compositional with the type-agreement guard; static invariant-offset read `TO3Spec`-only with the pg deferral |

Items 1, 3, 5 and 6 are also pinned by self-tests inside the two gate blocks
(the hook self-tests: positional alignment, cannot-override-composition,
throwing-discharger-degrades-to-abstain). (MEASURED.)

---

## 8. What is left, per discipline

**Stage 6 of the original plan — "retire the seam walkers" — is REMOVED as a
cross-cutting stage.** The layer choice is now per-discipline, equiv's is
decided (§6), and **neither remaining discipline inherits that answer**.

Both tracks below are **independent and may run in parallel**. Neither depends
on the other, and neither depends on further kit work.

### Track G — galilean, on its own terms

- **G1. Typed checking twin.** `checkDeclaredGal` beside the seam in the C1
  posture: no authority, CONFIRM/ABSTAIN/DISAGREE, disagreement is a compiler
  bug. Consumes the existing sgs stamps as axioms (§4.3).
  *Gate:* a new `blade test gal-check` census over every `__ml_galilean` decl
  including sgs-stamped ones. **Ship condition: 0 DISAGREE.** Expect
  abstentions on stamped sgs bodies and anything reaching `box_filter`.
- **G2. Typed deduction twin.** Proposals on an internal channel; BL4014 stays
  the seam's. The subset search (`EveryPasser`) ports as-is.
  *Gate:* a new `gal-differential` on `Test_RepDifferential`'s model — typed
  proposals ⊇ seam proposals, **zero false proposals**.
- **G3. The "preserves" vocabulary + `box_filter` stamp.** Only after G1
  *measures* the loss. A vocabulary change, which `SgsElaborate.fs:219-225`
  flags as a discipline change; owed its own review.
  *Gate:* gal-check abstentions drop by exactly the measured box_filter count.
- **G-layer. The layer decision.** Requires a **galilean rejection census** on
  R1's model — galilean owns 7 of the 47 reject-probes and none were analysed.
  Galilean does **not** inherit equiv's seam answer: its axioms live in sgs
  stamps that already survive to typecheck, which is the opposite of equiv's
  family C. Its answer could plausibly go the other way.

### Track P — perm, on its own terms

- **P1. The `__nodepow` index tag** (§4.4). Tag format, `IxKNodePow`, surface
  spelling, unifier arm, `derive_perm_*` emitters.
  *Gate:* an integrity family computed at registration (the `WignerTables`
  precedent); a typecheck fence test that a mis-tagged axis is refused; the
  existing perm corpus green with no message changes. **HIGH risk — a language
  change, not a checker change.**
- **P2. Typed checking + first-ever inference.** With the tag, perm's classifier
  is a tag read and its mode becomes signature-seeded.
  *Gate:* `perm-check` census, 0 disagree; plus a `perm-differential` that
  **degenerates** — there is no incumbent inference to compare recall against,
  so only the false-positive half is meaningful (every proposal's pinned twin
  must CHECK under the seam). That half is the one that matters.
- **P-layer.** Requires a **perm rejection census** (perm owns 8 of the 47
  probes). Also independent of equiv's answer.

---

## 9. Open questions, updated

1. ~~Is stage 6 (retire the seam) wanted at all?~~ **ANSWERED, per discipline.**
   For equiv: **no** — decided on R1's census, §6. For galilean and perm:
   deferred, and each needs its own census first (§8). The question is no longer
   global, which is the reframe's main consequence for planning.
2. ~~One vocabulary or three?~~ **ANSWERED: three**, by the user's decision of
   2026-07-29. Each equivariance has its own `ml.equiv`. §3.2 is the technical
   argument that agrees with it: the three claims are genuinely different
   theorems and a shared spelling would suggest a shared meaning they do not
   have. **Caveat carried forward:** G3's "preserves" claim is genuinely
   cross-cutting (equiv has status-preserving ops too), so if it is ever added
   it should be designed as a shape more than one vocabulary can spell — not as
   a fourth vocabulary, and not as a reason to re-merge the three.
3. **Dual certificates.** `ml.equiv(O3)` + `ml.perm_equiv(N)` on one function
   (§3.6's named "O(3)×Sₙ dual certificates") is unaffected by the reframe —
   arguably helped, since separate vocabularies compose more obviously than one
   overloaded one. The real obstacle is unchanged: MLPerm's v1 "one status per
   VALUE" limit (`MLPerm.fs:94-99`). Per-axis status vectors are the named v2
   for both, and are also §5.3's clearest reason to bypass `structuralArm`.
4. **Is `StructRules` the right granularity?** Two fields
   (`CovAppliedAsCallee`, `FormerConclusion`) is what one caller needed. Nothing
   proves it is right for two. Revisit at G1 — and prefer *adding a field* to
   *bending an arm*, since the kit is a library and a discipline that dislikes
   the shape can simply not call it.

---

## 10. Appendix — the `Discipline` record, as designed but NOT built

Retained for the record. It describes a genericized DRIVER, which stage 0
deliberately did not build (§7.3) and which the reframe makes optional rather
than inevitable. Build only against two witnesses that actually want it.

```fsharp
type Discipline<'Hyp, 'Cov, 'Fix> = {
    ConjunctName: string          // "__ml_equiv"
    SuggestCode: string           // "BL4011"
    FactKind: string              // deduced[].kind
    RenderHyp: 'Hyp -> string
    ParseHyp: string list -> 'Hyp option
    Lattice: LatticeOps<'Cov, 'Fix>
    // signature-level, NOT type-level — galilean forces this (§4.2)
    ClassifySig: 'Hyp -> (IRType -> IRType) -> string -> DParam list -> IRType
                     -> DSig<'Hyp,'Cov,'Fix> option
    ClassifyType: 'Hyp -> (IRType -> IRType) -> IRType -> Status<'Cov,'Fix>
    IsVacuous: DSig<'Hyp,'Cov,'Fix> -> bool
    Mode: HypothesisMode<'Hyp>    // SignatureSeeded | ClauseSeeded
    Select: Selection             // FirstPasser | EveryPasser
    Rules: Rules<'Cov, 'Fix>
}
```

**Summary keying, if it is ever built.** The shipped tables are
`TypeEnv.FuncRepSigs : Dictionary<IRId, RepSigT>` and `TypeEnv.FuncRepSpec`
(keyed `(groupStr, IRId)`). Note the plan calls this `FuncRepStatus`; **that
name does not exist in the tree** (MEASURED). A multi-discipline version needs
the discipline in the key — `(ConjunctName, IRId)` — because one function may
carry two certificates. Keying by **binder IRId** stays non-negotiable (the
`FuncSignParities` discipline, `TypeEnv.fs:221-235`): a parameter shadowing a
top-level function's name must not borrow its law. Since `DSig` is generic, one
heterogeneous dictionary needs boxing; the clean answer is one table per
discipline, which the reframe makes the natural shape anyway.
