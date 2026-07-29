# Design: is a DISCIPLINE data? — the three-way characterization

Status: DESIGN ONLY (2026-07-29). Nothing is ported; no checker changed
behavior; no gate moved. `src/DisciplineKit.fs` is a compiling SKELETON
(types + one generic walker fragment), wired into `Blade.fsproj` but called
from nowhere. Build verified clean.

Answers the stage-C3 question in `plan-equivariance-in-types.md` §2/§3:
*"A DISCIPLINE is data: a status lattice, a typed signature classifier, a
per-primitive transfer table, a claim vocabulary, and a mode. One generic
engine in Deduce.fs runs them all."* That claim was written with equiv in
hand and has never been tested against the two disciplines it was not
written for. This document tests it.

Claims are marked **MEASURED** (verified by reading the cited source or by
building) or **ARGUED** (a judgment from the evidence).

---

## 0. The verdict, up front

**The abstraction is real, but not at the layer §2 names, and it does not
reach as far as §2 hopes.**

Three findings, in decreasing order of confidence:

1. **The WALKER abstracts, and it is worth abstracting.** Roughly 250 of
   `DeduceRep.statusOf`'s 445 lines — including its single longest arm, the
   104-line interprocedural call rule — mention no discipline payload at
   all. I wrote them generically in `src/DisciplineKit.fs` and they compile.
   (MEASURED: build clean, §3.1 for the arm census.)

2. **The RULES do not abstract, and must not.** The three disciplines have
   *opposite polarity* at nearly every arithmetic arm, for good mathematical
   reasons: equiv's action is a **linear** block-diagonal rep, galilean's is
   an **affine shift**, perm's is a **permutation matrix**. `Cov + Cov` is
   legal for equiv, a *reject* for galilean, and legal for perm. Negation
   *preserves* for equiv and perm and *rejects* for galilean. No parameterized
   payload recovers this: they are different functions of the same type.
   (MEASURED: §3.2's table, every cell cited to a line.)

3. **Therefore "discipline as data" is true in the sense of "a record of ~8
   rule functions plus a classifier plus a hypothesis generator", and false
   in the sense of "one lattice with a swappable payload".** §2's phrasing is
   compatible with the first reading. Anyone reading it as the second will
   build the wrong thing. (ARGUED.)

Two corrections to the plan, both load-bearing:

- **§2's "Classifier: IRType -> status" is impossible for galilean** and must
  be lifted to signature level. Boost-variance is *deliberately not a type
  property* — a velocity and a velocity difference have the same type, and
  MLGalilean's header explains why units cannot be the seed. (MEASURED, §4.2.)
- **§0.2's stated mechanism for unblocking perm inference is wrong.**
  "Monomorphized extents make N concrete" does not hold: the blocker is that
  `16 = 2⁴ = 4²`, and monomorphization does not disambiguate that. The actual
  unblock available at typecheck is a *different* one — a parameterized index
  tag, propagated nominally by the unifier — and it is a type-system addition,
  not a free consequence of moving the walker. (MEASURED, §5.3.)

And one hard blocker, which is the most useful negative result here:

- **Moving galilean to typecheck LOSES the `sgs.box_filter` rule**, because
  box_filter is the one sgs former whose rule is status-*preserving*, the
  claim vocabulary has no spelling for "preserves", and so `SgsElaborate`
  deliberately does not stamp it. At the seam the rule is a surface-visible
  axiom; at typecheck it is an uncertified callee and a boost-variant argument
  declines. (MEASURED, §5.2.)

---

## 1. What already exists, and what it already decided

Before designing anything, note that **this abstraction has been attempted
once, at the seam, and the attempt deliberately stopped short of the rules.**

`src/ml/compiler/MLCertShell.fs` was extracted at the third witness
(`MLCertShell.fs:1-36`). Its header states the finding:

> WHAT DOES NOT LIVE HERE is every RULE: the status lattices, the signature
> classifiers, the judgment arms, the op tables, the diagnostics. The three
> lattices have OPPOSITE POLARITY at several arms (MLPerm's header tabulates
> it), so `judge` / `judgeStmts` / `judgeAssign` stay per-discipline. Their
> SHAPES do agree, but parameterizing `judgeStmts` would take a judge
> callback, an assign callback, the invariant status value, a variance
> predicate and two diagnostic constructors — six moving parts to share
> twenty-odd lines, which is a worse trade than the copy. Shell = the walk;
> module = the rules.

What the shell owns today: `judgeEach`, `patternVars`, `bindPatternVars`,
`freeVars`, `conjunctsOf`. Pure syntax. (MEASURED.)

**This design does not overturn that finding — it agrees with it, and
observes that the cost side of its trade inverts at typecheck.** The
argument was explicitly a *cost* argument ("six moving parts to share
twenty-odd lines"). At the seam, `judgeStmts` really is ~25 lines. At
typecheck the shared surface is not `judgeStmts`; it is the whole structural
half of a 445-line walker, and the interprocedural call rule alone is 104
lines of intricate, soundness-critical logic that all three disciplines
implement identically and that has *already* drifted between copies once
(MLPerm's header, the stage-5c drift catalog: two of the four findings were
false accepts, i.e. certificates issued for functions lacking the property).

The moral MLPerm's header draws is the design principle here:

> the copies drift in the GUARDS, not in the rules. Every divergence the diff
> found was a place where one walker checked something the others did not —
> never a place where two walkers checked the same thing and disagreed about
> the answer. The polarity table below is the intended disagreement; a guard
> only one copy has is a bug in the other two until argued otherwise.

That sentence *is* the abstraction boundary. Guards → shared. Rules → data.
(MEASURED quote; ARGUED reading.)

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

Actual definitions (MEASURED):

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

**Finding: the lattice abstracts cleanly**, as
`Status<'Cov,'Fix> = SCov of 'Cov | SFix of 'Fix | SOpaque | SBottom` with

| | `'Cov` | `'Fix` |
|---|---|---|
| equiv | `RepSpecT` | `InvShapeT` |
| galilean | `unit` | `unit` |
| perm | `int` (k ≥ 1) | `Sized \| Unsized` |

Two notes on faithfulness:

- **Perm's re-encoding is mechanical and sound.** MLPerm spells the invariant
  as `Pow 0`, sharing a constructor between the moving and fixed roles. But
  every arm in `MLPerm.judge` that matches `Pow 0` means *invariant*
  (`MLPerm.fs:586, 593, 602, 660`) and every arm that matches `Pow k when k > 0`
  means *covariant* (`MLPerm.fs:492, 549, 557, 621`). The split is already
  there in the source, spelled inside one constructor. (MEASURED.)
- **Galilean's empty payload is a real slot, not an absence.** `BVar` is
  documented as "tracks U0-coefficient EXACTLY 1" (`MLGalilean.fs:22`), and
  the named v2 is "rational U0-coefficient tracking" (`MLGalilean.fs:34-35`),
  which is precisely a payload. Today it is `unit`. (MEASURED.)

The lattice *algebra* needs two per-discipline functions — `JoinCov` (spec
equality / rank equality / trivially true) and `MeetFix`. That is
`LatticeOps` in the skeleton.

### 2.2 Classifier

| | classifies from | fully-annotated gate | ambiguous? |
|---|---|---|---|
| equiv | index-type family (`TyIrrepsIdx` / `TyPgIrrepsIdx`) | YES — `certSigOf` errors on any unannotated param (`MLEquiv.fs:430-431`) | no |
| galilean | **nothing typed** — the conjunct's parameter NAME list (`MLGalilean.fs:103-107`) | NO | no |
| perm | flat extent arithmetic, `powClass N M` (`MLPerm.fs:239-260`) | YES (`MLPerm.fs:328-329`) | **YES** |

This is the axis where §2 is wrong. See §4.2.

### 2.3 Transfer table

See §3.2. This is the axis where the disciplines genuinely, irreducibly
differ.

### 2.4 Claim vocabulary

Uniform already. All three are `WhereClause.Custom` entries — untyped
`(Ident * Ident list)` pairs (`Ast.fs:302-333`) — normalized by
`MLElaborate.normalizeConjunct` (`MLElaborate.fs:2017-2022`) to
`__ml_equiv` / `__ml_galilean` / `__ml_perm_equiv`, and read by the single
shared `MLCertShell.conjunctsOf`. (MEASURED.)

Note `Ast.Constraint.CnEquiv` exists but **has no constructor site** and is
documentation only (`Ast.fs:288-300`). Do not build on it.

Surfacing channels:

| | suggestion channel | code | structured fact |
|---|---|---|---|
| equiv | `Equiv.CertSuggestions` (`MLEquiv.fs:1537-1549`) | BL4011 | `CertFacts`, kind `"equiv"` |
| galilean | `Galilean.GalCertSuggestions` (`MLGalilean.fs:117-122`) | BL4014 | `CertFacts`, kind `"galilean"` |
| perm | **none** | BL4012 is an *error* code | **none** |

Both suggestion channels are `(string * Span) list` AsyncLocals, byte-identical
in shape. `CertFacts` is hosted *in MLEquiv* purely for F# compile order
(MLEquiv 114 < MLGalilean 115 < Ide 159), and MLGalilean writes into it.
(MEASURED.) A perm channel would face the same placement question.

### 2.5 Mode

| | gate | hypothesis space | selection |
|---|---|---|---|
| equiv | `import ml` **and** `candidatesFor ≠ []` (an irreps/pg family in the signature) | `[O3; SO3]` or `[Point g]` | **first passer** (strongest-first ladder) |
| galilean | `import ml` only — no signature gate at all | subsets of params: every singleton that occurs free, else the full occurring set once | **every passer** |
| perm | `import ml` **and** a non-empty cert table | *none* — no inference exists | n/a |

Two corrections to how §2/D2 describes this:

- **"Galilean's gateless sweep" is gateless only below the import gate.**
  `MLElaborate.expandModule` short-circuits the entire pass when no `import ml`
  alias exists (`MLElaborate.fs:2007-2013`), so all three are import-gated.
  Galilean's distinction is that it has no *signature-family* gate, and
  therefore attempts every function in an ml-importing module.
  (MEASURED.)
- **Selection differs for a principled reason, not an accidental one.** Equiv's
  candidates are competing strengths (proposing SO3 when O3 holds would make
  the dependency closure dishonest); galilean's are independent claims
  (`galilean(u)` and `galilean(v)` are both true and suppressing either hides a
  theorem — `MLGalilean.fs:477-480`). That is one record field, not two engines.
  (MEASURED quote; ARGUED conclusion.)

---

## 3. The evidence

### 3.1 The walker's structural/rule split — MEASURED

`DeduceRep.statusOf` (`DeduceRep.fs:512-980`) has 28 top-level arms. Sorting
them by whether they mention a discipline payload:

**STRUCTURAL (16 arms, ≈250 lines).** Written once in `DisciplineKit.structuralArm`
and compiling:

| arm | lines | why it is discipline-free |
|---|---|---|
| `TExprApp` | **104** | certified→speculative→all-fixed fall-through. Its soundness argument quantifies over *any* action: fixed inputs to a deterministic map give fixed outputs in every frame. |
| `TExprApply` (former) | 34 | bind kernel params to source statuses, walk body; the *conclusion guard* is the rule |
| `TExprBlock` | 35 | the statement fold |
| `TExprMatch` | 24 | scrutinee must be fixed; arms join |
| `TExprLambda` | 15 | capture scan |
| `TExprIf` | 11 | condition must be fixed; arms join |
| `TExprVar`, `TExprLet`, `TExprSequence`, `TExprField`, `TExprTupleIndex`, `TExprCompute`, self-guards, `_`| ~27 | plumbing |

**RULES (8 families, ≈100 lines).** `TExprLit`, `TExprBinOp` (43 lines),
`TExprUnaryOp`, `TExprArrayNegate`/`Conjugate`, `TExprIndex` (20), `TExprReduce`,
aggregates (`Tuple`/`Stack`/`Zip`/`ArrayLit`/`Join`), virtual arrays.

The ratio is the point: **the shared surface is 2.5× the per-discipline
surface, and the single most soundness-critical arm is on the shared side.**
Compare the seam, where the shared surface was ~25 lines of `judgeStmts` —
this is the quantified inversion of MLCertShell's trade. (MEASURED by line
range; ARGUED as to significance.)

That all three seam checkers implement the structural arms *identically* is
also measured, not assumed — e.g. the if-condition rule:

```fsharp
// MLEquiv (via joinStatus), MLGalilean.fs:198-206, MLPerm.fs:590-598
| ExprKind.ExprIf (c, t, f) ->
    j c |> Result.bind (fun sc -> match sc with
        | BInv (* / Pow 0 / Inv _ *) -> ...arms must agree...
        | _ -> reject "...branching on a frame-dependent value...")
```

### 3.2 The polarity table — MEASURED

MLPerm's header (`MLPerm.fs:61-75`) tabulates equiv vs perm. Reading
MLGalilean beside them completes it. Every cell is cited.

| rule | equiv | galilean | perm |
|---|---|---|---|
| `Cov + Cov` (same payload) | **legal** → Cov <br/>`DeduceRep.fs:569` | **REJECT** — "doubles the U0-coefficient" <br/>`MLGalilean.fs:184-185` | **legal** → Pow k <br/>`MLPerm.fs:581-582` |
| `Cov − Cov` | legal → Cov <br/>`DeduceRep.fs:569` | **legal → FIXED** — "THE rule: the boost cancels" <br/>`MLGalilean.fs:183` | legal → Pow k |
| `−Cov` (negate) | **legal**, preserves (−I commutes with every D) <br/>`DeduceRep.fs:600` | **REJECT** — "flips its U0-coefficient to −1" <br/>`MLGalilean.fs:172` | **legal**, preserves (pointwise) <br/>`MLPerm.fs:512-514` |
| scalar · Cov | legal *iff provably scalar* <br/>`DeduceRep.fs:578-580` | **REJECT** <br/>`MLGalilean.fs:192-193` | legal (broadcast) <br/>`MLPerm.fs:581` |
| `Cov · Cov` | **REJECT** — that is CG's job <br/>`DeduceRep.fs:572` | REJECT | **legal** → Pow k <br/>`MLPerm.fs:575-582` |
| pointwise nonlinearity on Cov | **REJECT** <br/>`DeduceRep.fs:601-604` | REJECT | **legal** <br/>`MLPerm.fs:64` |
| component read of Cov | **REJECT**, except a static offset in a trivial block <br/>`DeduceRep.fs:721-734` | **legal → Cov** — "per-component and index-stable" <br/>`MLGalilean.fs:401-406` | REJECT in v1 (legal in the maths) <br/>`MLPerm.fs:111-121` |
| aggregate of Covs | **REJECT** <br/>`DeduceRep.fs:522-527` | **legal → Cov** if uniform <br/>`MLGalilean.fs:152-161` | REJECT + extent test <br/>`MLPerm.fs:483-499` |

**Why, mathematically** (ARGUED, but the source states each premise):

- equiv's action is **linear**: `x ↦ D·x`. Linear maps commute with `+`, `−`
  and scalar `·`, so the moving set is a linear subspace — closed under
  addition. They do *not* commute with nonlinearities or with products.
- galilean's action is an **affine shift**: `u ↦ u + U₀`. Affine shifts do not
  commute with addition at all; the moving set is a *torsor*, and differences
  land in the fixed space. Hence the unique `Cov − Cov → Fix` rule and the
  rejection of `Cov + Cov`.
- perm's action is a **permutation matrix** — monomial, 0/1. It moves cells
  without mixing them, so it commutes with *every* pointwise map. Hence
  products and nonlinearities are legal, which is exactly what equiv forbids
  (`MLPerm.fs:71-75`: "One judgment cannot wear both").

**This is the negative result.** These are three different algebraic
structures, not three payloads. Any design that tries to derive the arithmetic
rules from a shared parameterized action will be wrong for two of the three.

---

## 4. The design

`src/DisciplineKit.fs` — compiling, wired into `Blade.fsproj`, called by
nothing. Full types there; the shape here.

### 4.1 The record

```fsharp
type Discipline<'Hyp, 'Cov, 'Fix> = {
    // claim vocabulary
    ConjunctName: string          // "__ml_equiv"
    SuggestCode: string           // "BL4011"
    FactKind: string              // deduced[].kind
    RenderHyp: 'Hyp -> string
    ParseHyp: string list -> 'Hyp option
    // lattice
    Lattice: LatticeOps<'Cov, 'Fix>          // JoinCov, MeetFix, FixTop
    // classification  (NOTE: signature-level — see §4.2)
    ClassifySig: 'Hyp -> (IRType -> IRType) -> string -> DParam list -> IRType
                     -> DSig<'Hyp,'Cov,'Fix> option
    ClassifyType: 'Hyp -> (IRType -> IRType) -> IRType -> Status<'Cov,'Fix>
    IsVacuous: DSig<'Hyp,'Cov,'Fix> -> bool
    // mode
    Mode: HypothesisMode<'Hyp>    // SignatureSeeded | ClauseSeeded, same fn type
    Select: Selection             // FirstPasser | EveryPasser
    // rules — the polarity table, as data
    Rules: Rules<'Cov, 'Fix>
}
```

**What is a type parameter vs a function parameter.** Three type parameters,
each earning its place: `'Cov` because the payloads are structurally unrelated
(a block spec, unit, an int); `'Fix` because refinements are (scalar-vs-
aggregate, unit, sized-vs-unsized); `'Hyp` because hypotheses are (a group, a
velocity-name set, an extent). Everything else is a function field, because
everything else is *behavior selected per discipline*, and a function field is
how you say that without a type-class hierarchy F# would make painful.

**How the walker stays generic over the lattice.** It never pattern-matches a
payload. It matches only the four `Status` constructors, and delegates every
payload comparison to `LatticeOps.JoinCov` and every payload *rule* to
`Rules`. `structuralArm` returns `Status option`; `None` **is** the
abstraction boundary, declared in one place and checkable by reading one
function. (MEASURED: it compiles with no discipline mentioned.)

**How summaries are keyed when disciplines coexist.** The shipped tables are
`TypeEnv.FuncRepSigs : Dictionary<IRId, RepSigT>` (certified) and
`TypeEnv.FuncRepSpec : RepSpecTable` (speculative, keyed
`(groupStr, IRId)`). Note: the plan calls this `FuncRepStatus`; **that name
does not exist in the tree** (MEASURED). Generalizing:

- **Certified** → `Dictionary<string * IRId, DSig<...>>`, keyed
  `(ConjunctName, binder id)`. A function may carry `ml.equiv(O3)` *and*
  `ml.perm_equiv(4)` — §3.6's named "O(3)×Sₙ dual certificates" — so the
  discipline must be in the key, not implied by the table.
- **Speculative** → the existing `(hypothesisString, IRId)` key, extended to
  `(ConjunctName, hypothesisString, IRId)`. `RenderHyp` supplies the middle
  component for all three, which is why it is on the record.
- Keying by **binder IRId** stays non-negotiable — the `FuncSignParities`
  discipline (`TypeEnv.fs:221-235`): a parameter shadowing a top-level
  function's name must not borrow its law.

Because `DSig<'Hyp,'Cov,'Fix>` is generic, one heterogeneous dictionary is not
directly expressible without boxing. The clean answer is **one table per
discipline instance**, held in a per-discipline registration record, with the
`(ConjunctName, IRId)` key preserved for the dual-certificate case. That is a
small amount of plumbing, and it is honest about the fact that two disciplines
cannot read each other's summaries anyway (they mean different things).

### 4.2 The forced shape change: classification is signature-level

§2 specifies `Classifier: IRType -> status`. That is right for equiv and perm
and **impossible for galilean**:

```fsharp
// MLGalilean.buildCertTable — MLGalilean.fs:103-107
let ps =
    fd.Params
    |> List.map (fun p ->
        (p.Name, if List.contains p.Name args then BVar else BInv))
```

Boost status comes from the *conjunct's argument list*, positionally. And it
cannot come from a type, by design (`MLGalilean.fs:17-20`):

> Units are deliberately NOT the seed: a velocity DIFFERENCE still carries the
> velocity unit but is boost-invariant — units track dimension, not frame
> behavior. The conjunct names the boost-variant parameters.

So the classifier must be `'Hyp -> DParam list -> IRType -> DSig option` —
whole-signature. equiv and perm implement it by mapping a per-type classifier
(and both keep one, as `ClassifyType`, which the former-conclusion guard and
the free-variable arms need anyway). Galilean implements it by consulting its
hypothesis. **Lifting admits three; keeping it at the type admits two.**
(MEASURED premise, ARGUED conclusion.)

### 4.3 The equiv instance — a refactor that loses nothing

Equiv maps onto the record with no residue:

| field | today |
|---|---|
| `'Hyp` / `'Cov` / `'Fix` | `GroupT` / `RepSpecT` / `InvShapeT` |
| `ConjunctName` / `SuggestCode` / `FactKind` | `"__ml_equiv"` / `"BL4011"` / `"equiv"` |
| `RenderHyp` / `ParseHyp` | `groupStrT` / `groupOfName` (`DeduceRep.fs:130-135`) |
| `Lattice` | `JoinCov = (fun a b -> if a = b then Some a else None)`; `MeetFix = meetShapeT`; `FixTop = TInvShapeUnknown` |
| `ClassifySig` / `ClassifyType` / `IsVacuous` | `classifySignature` / `classifyType` / `isVacuous` |
| `Mode` / `Select` | `SignatureSeeded candidatesFor` / `FirstPasser` |
| `Rules` | the 8 rule arms, lifted verbatim out of `statusOf` |

The one piece with nowhere to live in the record is the **engine hook**
(`EngineDischarge`, `DeduceRep.fs:1084-1109`) — the polynomial extractor that
discharges bodies composition cannot judge. It is genuinely equiv-only (it is
a *polynomial* normal-form argument about `IrrepsIdx` blocks). It should be an
`EngineHook option` field on the record: `None` for galilean and perm, which
is exactly the posture C1 shipped with before C2 filled the slot.

Refactor risk is low and the gate is exact — see stage 0.

### 4.4 The galilean instance

`Status<unit, unit>`. `JoinCov = fun () () -> Some ()`. `FixTop = ()`.
`Mode = ClauseSeeded` with the subset search. `Select = EveryPasser`.
`ClassifySig` reads the hypothesis's name set. `Rules` are the eight arms of
`MLGalilean.judge`, which are *already* written as a table.

`DSig.Return` is present even though `GalSig` has no return field: galilean's
v1 rule is "certified functions return boost-invariant"
(`MLGalilean.fs:450-453`), which is a fixed *value* of that field, not a
missing field. Making it explicit is what lets one engine compare body-status
against return-status for all three.

**Where the instance does not fit — see §5.2.** Galilean is portable, but not
without loss, and the loss is in the vocabulary rather than the engine.

### 4.5 The perm instance

`Status<int, PermFix>` where `PermFix = Sized | Unsized`. Re-encoding per
§2.1. `Rules` are the arms of `MLPerm.judge`. `Mode`: today `ClauseSeeded`
with an empty generator (no inference exists). **After the tag work of §5.3 it
becomes `SignatureSeeded`, and inference becomes possible for the first
time** — which is the whole prize.

---

## 5. What each discipline needs from the type system

### 5.1 equiv — already has it

`IrrepsIdx`/`PgIrrepsIdx` ride `IRIndexTypeG.Tag` through unification
(`Types.fs:157-228`, `Unify.fs:506-521`). Ported in phases B/C1/C2. The typed
side is *stronger* than the seam in one measured place: `IRTScalar` is
provably 0-dimensional, where the surface classifier had to guess from builtin
names (`DeduceRep.fs:243-256` vs `MLEquiv.isBuiltinScalarName`).

### 5.2 galilean — needs nothing from types, and loses an axiom

Galilean gains from the move: binder-IRId keying (shadowing safety), the
`flattenBindings`/desugaring handling, and shape facts it does not currently
use. It needs no type-system change, because its property is not a type
property.

**But three of its rules are surface-visible sgs formers, and they do not
survive.** `MLGalilean.judgeApp:353-375` carries axioms for `sgs.grad`,
`sgs.stress`, `sgs.box_filter`. By typecheck, sgs has elaborated
(`TypeCheck.fs:10803` ml, then `10809` sgs) and those calls are loop nests.

Two of the three are already rescued, and this is the good news: **SgsElaborate
already stamps `__ml_galilean` onto the functions it synthesizes**
(`SgsElaborate.fs:227`, `galileanStamp`). Plan A1 asked someone to "verify the
sgs elaborator's synthesized stencils can carry galilean stamps the same way".
They already do. Those stamps land in the certified table by the same conjunct
path equiv's stamps use, so at typecheck `grad` and `stress` become certified
callees — the axiom becomes a table lookup, which is *better* than the seam's
name-matching.

**`box_filter` is the exception, and it is a designed one**
(`SgsElaborate.fs:219-225`, MEASURED):

> WHY box_filter IS NOT STAMPED. It is the one former whose seam rule is
> STATUS-PRESERVING rather than invariant-producing: `sgs.box_filter(U, W)`
> maps a boost-variant field to a boost-variant field (the weights sum to 1,
> so filter(u + U0) = filter(u) + U0). A `__ml_galilean` certificate asserts a
> boost-INVARIANT result, so stamping it would be a false axiom, not a weaker
> one. The v1 claim vocabulary has no spelling for "preserves"; giving it one
> is a discipline change, not a stamping change.

The emitter confirms it precisely: all three ops become generated function
decls, but only two are stamped (MEASURED, `SgsElaborate.fs:262, 268, 281`):

```fsharp
| "grad", ...        -> ensure st ... (fun nm -> galileanStamp [ "u" ] (gradDecl nm n))
| "box_filter", ...  -> ensure st ... (fun nm -> boxFilterDecl nm n w)     // no stamp
| "stress", ...      -> ensure st ... (fun nm -> galileanStamp [ "u" ] (stressDecl nm n w))
```

So: at the seam, `box_filter(u, w)` with `u : BVar` yields `BVar`. At
typecheck it is an **uncertified callee** and a `BVar` argument **declines**
(`DisciplineKit.structuralArm`'s all-fixed rule; `DeduceRep.fs:859-867`).
That is a real recall regression, it is measurable, and fixing it means adding
a *preserves* claim to the vocabulary — which the source itself classifies as
a discipline change. **This is the seam that forces galilean partly out**, and
it is why §6 stages it separately rather than folding it into the port.

There is a second, milder consequence of the same ordering. SgsElaborate notes
that sgs running *after* ml is "what keeps their bodies — flat work arrays and
index arithmetic the composition walker would refuse — out of a judgment they
were never meant to face". At typecheck those bodies *are* in scope of the
validator. Expect abstentions, exactly as equiv measured (63 of its 76
abstentions are generated `derive_*` bodies, per the plan's C2 note). Benign
and expected; the census must simply record it rather than be surprised by it.

### 5.3 perm — the plan's mechanism is wrong; the real one is a new tag

**§0.2 claims:** *"`perm_equiv`'s flat-extent keying is ambiguous at a surface
signature; at typecheck, monomorphized extents make N concrete. The strongest
argument for the user's thesis: the exact type literally unblocks the
deferral."*

**This does not hold as stated.** Two distinct questions are being conflated:

1. *Is the extent M concrete?* At the seam, `statusOfIndex` already resolves it
   with `evalExpr statics fuel extentE` (`MLPerm.fs:242-247`). For a
   non-polymorphic decl it is already concrete. Monomorphization helps only
   for size-polymorphic signatures — a real but narrow recall gain.
2. *Given M, what is N?* `powClass N M` finds k with `N^k = M`. For inference
   N is **not** given — it comes from the conjunct
   (`ml.perm_equiv(4)`, `MLPerm.resolveN:285-301`). And a signature does not
   determine it: an `Idx<16>` axis reads as `Pow 4` at N=2, `Pow 2` at N=4,
   `Pow 1` at N=16. **Monomorphizing 16 does not disambiguate 16.**

Question 2 is the actual blocker, and MLEquiv's own comment says so
(`MLEquiv.fs:1605-1607`, MEASURED): *"guessing N from an `Array<_ like
Idx<n>>` would propose noise."* Compounding it, MLPerm documents an
**extent-keying caveat** (`MLPerm.fs:101-108`): a weight buffer whose extent is
*coincidentally* `N^k` classifies covariant. Its own named fix is *"Nominal
keying (`Nat<Node>`) is the named upgrade."*

**What typecheck actually offers is that upgrade.** `IRIndexTypeG.Tag` is
described as "Name (for index space matching)" (`Types.fs:447`), a named index
type sets `Tag = Some name` (`TypeCheck.fs:772, 9991, 10056`), and — critically
— the unifier treats user-named tags **nominatively** (`Unify.fs:519`,
MEASURED):

> User-named tags are nominative: lat != lon even if both Idx<180>.

That is a guarantee the surface signature cannot express and the seam cannot
rely on: at the seam a type alias is transparent and nothing makes call sites
respect it; at typecheck the unifier *enforces* it.

**The design consequence — and it is a type-system addition, not a free win.**
Nominal names alone are not enough, because perm keys on *flat* `N^k` axes,
and nothing relates a hypothetical `Node2 = Idx<16>` to `Node = Idx<4>`. What
perm needs is its own **parameterized tag**, minted exactly the way
`mkIrrepsTag` is (`Types.fs:157-163`), carrying both numbers:

```
"__nodepow:<N>:<k>"
```

with an `IxKNodePow` kind beside `IxKIrreps`/`IxKPgIrreps`. With that:

- classification is a **tag read**, not extent arithmetic → question 2
  disappears; N comes out of the type;
- perm's `Mode` becomes `SignatureSeeded` exactly like equiv, and **inference
  is unblocked** — the §0.2 prize, for the right reason;
- the coincidental-extent caveat disappears, because a Bell(2)-sized weight
  buffer carries no `__nodepow` tag;
- spec identity rides in the tag, so `Pow k` agreement is tag equality, which
  unification already propagates for free.

The precedent is exact: this is the same move `IxKPgIrreps` made as a "twin,
not reroute" beside `IxKIrreps` (`Types.fs:113-117`). But it touches the
surface language (an index-type spelling) and the `derive_perm_*` emitters, so
it is its own high-risk stage. **Honest summary: perm on the typed engine is
blocked on a type-system addition; the move alone buys it nothing beyond
polymorphic-extent recall.**

---

## 6. Staged port plan

Every stage is independently shippable and independently gated. The two
existing gates are the model: a **differential** against the incumbent, plus a
**census**. Ordered by risk.

### Stage 0 — land the kit; refactor equiv onto it *(sequential; blocks all)*

Make `DisciplineKit` real and re-express `DeduceRep` as its equiv instance.
No new discipline, no new recall, no behavior change.

- **Gate:** `blade test rep-differential` stays 16/16 matched, 0 exempt; `blade
  test rep-check` stays 130 decls / 54 confirm / 76 abstain / 0 disagree
  (the plan's C1+C2 numbers). **Byte-identical counts are the ship condition** —
  anything else means the refactor lost something.
- **Risk:** LOW–MEDIUM. Mechanical, exactly gated.
- Must be sequential: every later stage builds on the record's final shape.

### Stage 1 — galilean typed CHECKING twin *(parallelizable with 2)*

`checkDeclaredGal` beside the seam, the C1 posture exactly: no authority,
CONFIRM/ABSTAIN/DISAGREE, disagreement is a compiler bug (BL9004,
build-stopping). Consumes the existing sgs stamps as axioms.

- **Gate:** NEW `blade test gal-check` — a census over every `__ml_galilean`
  decl including sgs-stamped ones. **Ship condition: 0 DISAGREE.** Abstentions
  are recorded and split generated-vs-source, the way `RepCheckCensus` does
  (`DeduceRep.fs:1128-1161`).
- **Expected, not a failure:** abstentions on the stamped sgs bodies (§5.2),
  and on every body reaching `box_filter`.
- **Risk:** MEDIUM.

### Stage 2 — galilean typed DEDUCTION twin *(parallelizable with 1)*

Proposals only, on an internal channel; BL4014 stays the seam's. The subset
search (`EveryPasser`) ports as-is.

- **Gate:** NEW `blade test gal-differential`, modeled on `Test_RepDifferential`:
  typed proposals ⊇ seam proposals over the whole corpus, **zero false
  proposals** (every proposal's pinned twin must check under the seam).
- **Risk:** MEDIUM.
- Shares stage 1's instance; the two agents must agree the instance first, then
  can work independently (1 owns the checking site, 2 owns the deduction site —
  the same split C1 and C2 ran).

### Stage 3 — the "preserves" claim vocabulary + box_filter stamp *(after 1)*

Only after stage 1 has *measured* the box_filter loss. Adds a preserving
claim spelling and lets SgsElaborate stamp box_filter.

- **Gate:** gal-check abstentions drop by exactly the measured box_filter
  count; gal-differential unchanged or improved.
- **Risk:** MEDIUM–HIGH — a vocabulary change, which `SgsElaborate.fs:219-225`
  explicitly flags as a discipline change rather than a stamping change. Owed a
  design review of its own.
- Sequential after 1 (it needs 1's measurement to be justified at all).

### Stage 4 — the `__nodepow` index tag *(parallelizable with 1–3; blocks 5)*

A type-system addition: tag format, `IxKNodePow`, surface spelling, unifier
arm, and `derive_perm_*` emitters stamping it. Independent of every checker.

- **Gate:** a seventh integrity family computed at registration (the A3/
  `WignerTables` precedent named in the plan §6.3); a typecheck-fence test that
  a mis-tagged axis is refused; the whole existing perm corpus stays green with
  no message changes.
- **Risk:** HIGH — it changes the language, not a checker. Different subsystem
  from stages 1–3, so a different agent can own it concurrently.

### Stage 5 — perm typed checking + first-ever inference *(after 0 and 4)*

With the tag, perm's classifier is a tag read and its mode flips to
`SignatureSeeded`.

- **Gate:** `blade test perm-check` census, 0 disagree; plus a
  `perm-differential`. Note the differential **degenerates**: there is no
  incumbent inference to compare recall against, so only the false-positive
  half is meaningful — every proposal's pinned twin must CHECK under the seam
  checker. That half is the one that matters here.
- **Risk:** MEDIUM once stage 4 lands.

### Stage 6 — retire the seam walkers *(last; sequential)*

The actual C3 flip: MLElaborate keeps synthesis + stamping only.

- **Gate:** full corpus + the 92-file SUGGEST block green, **plus a
  message-text diff**. This is the real hazard: every `rejects` corpus pin is
  seam-worded today, and the typed walkers' diagnostics are new strings.
- **Risk:** HIGH. Must be last, must be sequential.

### Parallelism summary

```
        Stage 0  (sequential, blocks all)
           |
     +-----+-----+---------------------+
     |           |                     |
  Stage 1     Stage 2               Stage 4   (independent subsystem)
     |                                  |
  Stage 3                            Stage 5
     |                                  |
     +----------------+-----------------+
                      |
                   Stage 6  (sequential, last)
```

Three agents can work concurrently after stage 0: one on galilean checking
(1→3), one on galilean deduction (2), one on the node-power tag (4→5).

---

## 7. Open questions this design does not settle

1. **Is stage 6 wanted at all?** The plan's §5.3 already asks whether
   "B-forever" — deduction typed, checking at the seam — is a stable end
   state. Stages 0–5 deliver every capability gain (galilean recall, perm
   inference) *without* the diagnostic-parity risk. Stage 6 buys only the
   deletion of the seam walkers. That is a real benefit (three copies that
   have drifted once already) but it is a maintenance benefit, not a
   capability one, and it carries the highest risk in the plan.
2. **One vocabulary or three?** §6.4 q3 asks whether sharing an engine argues
   for one pin spelling. This design says **no** — the polarity table shows the
   three claims are genuinely different theorems, and a shared spelling would
   suggest a shared meaning they do not have. But stage 3's "preserves" claim
   *is* cross-cutting (equiv has status-preserving ops too), so the vocabulary
   question should be settled there rather than deferred.
3. **Dual certificates.** The `(ConjunctName, IRId)` summary key is designed
   for `ml.equiv(O3)` + `ml.perm_equiv(N)` on one function (§3.6's named
   "O(3)×Sₙ dual certificates"), but nothing in this design *implements* the
   interaction, and MLPerm's v1 "one status per VALUE" limit
   (`MLPerm.fs:94-99`) is the real obstacle. Per-axis status vectors are the
   named v2 for both.
4. **Is `Rules` the right granularity?** Eight fields is a judgment call. A
   finer split (per-operator rather than per-node-kind) would share more but
   force disciplines to answer questions they do not have opinions about; a
   coarser one collapses toward "write your own walker". Nothing here proves
   eight is optimal — only that it is sufficient for three witnesses.
