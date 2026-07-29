# Census: rejection parity between the seam checker and the typed walker

Measured 2026-07-29 on branch `r1-rejectparity`, at `18b1f06` + the C1/C2
stitch (`ed71c9c`). Harness: `tests/Test_RepRejectCensus.fs`, verb
`blade test rep-reject`. Every number below is reproduced by that block on
every run; nothing here is estimated.

**Scope.** This measures the gate for stage C3 of
`docs/plan-equivariance-in-types.md` — retiring the elaboration-seam walkers
and making `src/DeduceRep.fs` the checking authority. It changes no checking
behaviour, adds no verdict to the lattice, and does not touch
`src/ml/compiler/MLEquiv.fs`.

**Claim labelling.** Every claim is marked **[MEASURED]** (produced by the
harness or by reading the code path it exercises) or **[ARGUED]** (a judgement
about what the measurement implies).

---

## 0. Headline

| Question | Answer |
|---|---|
| `(rejects)` probes in `tests/corpus/ml-equiv` | 47 of 105 files **[MEASURED]** |
| …refused on the **equiv** channel (BL4008) | **30** |
| …refused by a **different discipline** (BL4009 galilean 7, BL4012 perm 8) | 15 |
| …refused by machinery that is not an equivariance walker (BL4007 1, BL4003 1) | 2 |
| Rejections where the typed walker runs **at all** today | **1 of 47** |
| Of the 30 equiv rejections, typed side would **still refuse** | **9** |
| …would **compile** (typed abstains) | **15** |
| …would **compile** (typed *confirms* — the alarming case) | **1** |
| …are refused by a later stage anyway, with a different code | **5** |
| ml-equiv message pins that would need rewording | **0** (the category pins no message text) |
| `tests/corpus/diagnostics` BL4008 `ERROR-CONTAINS` pins surviving a flip | **2 of 37** |
| …BL4008 span pins surviving | **7 of 13** (coincidentally — see §5) |

**The one-line verdict [ARGUED]:** the flip is *not* reachable as a swap. Half
the equiv rejections (16 of 30) would silently start compiling, and 35 of 37
worded diagnostic pins would die, because the typed lattice's refusal value
carries neither a cause nor a span. What *is* reachable, and cheaply, is a
staged path that closes three of the six families and leaves two at the seam
permanently. See §7.

---

## 1. The structural fact that had to be established first

`Blade.ML.Elaborate.expand` runs **before** `checkProgram` inside
`TypeCheck.typeCheck` (`src/TypeCheck.fs`, the elaboration cascade around
line 10803). A seam rejection makes it return `Error`, and `typeCheck` returns
immediately.

**[MEASURED]** On 46 of the 47 reject-probes the live C1 census is
`0 confirm / 0 abstain / 0 disagree`: the typed walker is never invoked. The
single exception is `016_layer_binding_wrong_spec` (BL4003), whose rejection
happens *at* typecheck — elaboration succeeded, `checkProgram` ran, the typed
walker recorded 1 abstain, and only then did the index-type mismatch fire.
(That one abstain is exactly the arithmetic that reconciles this document's
`54 confirm / 75 abstain` over the 58 accepted files with C1's
`54 / 76` over all 130 certified decls.)

**[MEASURED]** The two sides cannot be decoupled by renaming the pin.
`MLEquiv.buildCertTable` looks for `conjunctsOf "__ml_equiv"` and TypeCheck's
C1 site looks for `customConjuncts |> tryFind (n = "__ml_equiv")` — the same
normalized key, by design. No spelling reaches one and not the other.

**[ARGUED]** Consequence for planning: there is no incremental "shadow mode" in
the compiler today. Any C3 work that wants to compare the two sides on refused
programs must first add one (a flag that demotes the seam's BL4008 to a note),
or repeat this document's source-rewrite trick.

### How the census got a verdict anyway

The harness rewrites `ml.equiv(G)` in the source to a test-registered inert
conjunct (`__rep_reject_census_shadow`). `buildCertTable` then finds no
certified functions, the whole equiv judgment short-circuits
(`if Map.isEmpty certs then []`), and everything else — statics, `derive_*`
synthesis, elaborator stamping, unification — is unchanged. The program reaches
typecheck; the harness then re-runs `DeduceRep.checkDeclaredRep` out of band on
the resulting `TypedProgram`, supplying the group the shadowed conjunct still
carries.

**The method is itself measured [MEASURED].** The out-of-band re-run differs
from the production site in two ways (it passes `id` for `resolve` rather than
the live `Subst.Resolve`, and it rebuilds the certified-signature table by
walking decls). Obligation 3 of the harness asserts that on **all 58 accepted
files** the out-of-band `(confirm, abstain, disagree)` triple equals the triple
the production site recorded — file for file, `54 / 75 / 0`. It does.

**Caveat [ARGUED].** That calibration necessarily covers only programs the seam
accepts; on the reject probes there is no live census to compare against. The
pipeline is the same and the same code path is exercised, so the risk is low,
but it is not zero.

---

## 2. Census by seam channel

**[MEASURED]**, `blade test rep-reject`:

| Channel | Files | In scope for the equiv flip? |
|---|---|---|
| `equiv` (BL4008) | 30 | yes — §3 |
| `galilean` (BL4009) | 7 | no — a different discipline with no typed lattice at all |
| `perm` (BL4012) | 8 | no — same |
| `other:BL4007` | 1 | no — `derive_linear` Schur refusal, an *op-synthesis* error |
| `other:BL4003` | 1 | no — index-type mismatch, an ordinary typecheck error |

Galilean and perm files: 027, 028, 029, 030, 031, 081, 083 (galilean);
042, 043, 044, 066, 067, 068, 075, 076 (perm). BL4007: 014. BL4003: 016.

**[ARGUED]** 17 of the 47 `(rejects)` probes are not part of this gate. Two of
them (014, 016) are not about certificates at all and would be unaffected by
any flip. The other 15 belong to C3's *other* half — the plan's "galilean and
perm land as discipline instances on the generic engine" — and each would need
its own census before that half is scheduled. Nothing in this document
measures them.

---

## 3. The 30 equiv rejections, by cause

Per-file verdict is decided by the functions the seam actually **named** in its
diagnostics (its messages all open `function '<name>'`), because several probes
deliberately place a good function beside the bad one.

### Family A — the polynomial engine refutes it → typed **DISAGREE** (9 files)

004, 005, 060, 062, 064, 065, 070, 098, 099. **[MEASURED]**

The typed side reaches `RepDisagree` through `checkDeclaredRep`'s
`engineFallback` arm: composition declined (`TBottom`/`TOpaque`), the C2 engine
was consulted, and it *refuted*. These programs would still be refused after a
flip.

Two of them are worth naming **[MEASURED]**: 004 (`x * y` on two rep-typed
values) and 070 (a former whose kernel sees rep components) are refused by the
seam at **composition**, with targeted messages ("elementwise product of
representation-typed values is not equivariant — use ml.tensor_product"), but
the typed side reaches them through the **engine**, with the generic
"the body IS a polynomial and it is not O3-equivariant" text. Same verdict,
completely different explanation.

Two files in this family also carry an offender the typed side *abstains* on:
062's `quintic` and 065's `quintic`, both over the engine's degree-4 cap. The
seam covers those with `Engine.capNote` ("the equivariance engine did not run
on 'quintic' … the verdict above is composition's"), which composition supplied;
the typed side has no composition message, so those two functions slip. The
files stay refused only because a *different* function in them disagrees.

### Family B — composition refuses, and the typed walker throws the refusal away → typed **ABSTAIN** (7 files)

007, 023, 036, 050, 069, 072, 088 (plus 062/065's `quintic`, counted in A).
Abstain reason: `walker declined (outside the composition fragment)` — i.e.
`statusOf` returned `TBottom`. **[MEASURED]**, 10 offender-verdicts.

**This is the family the whole flip turns on [ARGUED].** `TBottom` is produced
at 25 distinct sites in `DeduceRep.statusOf` **[MEASURED]**, and it is exactly
where the seam emits one of its 33 `bl4008` construction sites outside the
`Engine` module **[MEASURED]** (37 in the file overall; 4 are the engine's). The typed walker *has already decided* that the body is not
equivariant at these sites; it is the lattice value, not the analysis, that
discards the reason: `RepStatusT.TBottom` carries no payload, and `RepCtx` has
no diagnostic channel at all.

The seam's messages here are the domain-teaching ones — "representation-typed
value escapes to 'helper', which carries no equiv certificate",
"irreps_to_sym reads basis-dependent Cartesian components out of a
representation", "raw indexing into an l>0 … component of 'k' reads a
basis-dependent number", "only an invariant SCALAR may scale a
representation-typed value, and this invariant is an array". Reproducing them
means giving `TBottom` a cause **and a span**, one refusal site at a time.

### Family C — the ml vocabulary is gone by typecheck → typed **ABSTAIN** (6 files)

008, 035, 038, 087, 091, 105. Abstain reason:
`nothing established for the body` — i.e. `statusOf` returned `TOpaque`.
**[MEASURED]**

Every one of these rejects an argument to a *surface `ml.*` op*: `ml.gated` on
a spec with an (l=0, odd) block; `ml.derive_sym_tp` / `ml.derive_poly` /
`ml.derive_linear` handed an invariant where a rep is required; an invariant of
unknown shape scaling a rep inside `ml.norms`. By typecheck those calls have
been rewritten into calls to generated `__ml_N` functions — plan §1's stated
reason the walkers live at the seam, showing up here as a measured family.

**[ARGUED]** This family is *not* hopeless: the generated callee carries a
stamped certificate whose parameter statuses record what it needed, so a
rep/invariant mismatch at the call is in principle visible. What is missing is
**provenance**: the typed side can only name `__ml_1`, not
`ml.derive_sym_tp(SA, xbad, y, w)`. Closing it means extending the A1 stamp
with the originating op name and span.

### Family D — the refusal is in the SIGNATURE → typed **ABSTAIN** (2 files)

048, 049. Abstain reason:
`signature not classifiable by the typed classifier`. **[MEASURED]**

048 is the shape the brief predicted: an `IrrepsIdx` parameter under an
`ml.equiv(C4)` certificate. 049 is its twin: a `PgIrrepsIdx<D4, …>` parameter
under `ml.equiv(C4)`.

**[ARGUED] This is the cheapest family to close, by a wide margin.**
`DeduceRep.classifyType` *already* returns `TOpaque` at precisely the offending
position and for precisely the seam's reason — the `GPoint pg` arm has explicit
cases for "an O(3) irreps axis under a point-group certificate" and "another
group's PgIrrepsIdx", each with a soundness comment that is nearly the seam's
message. `classifySignature` already knows which parameter failed; it just
collapses the whole thing to `None`. Widening `classifySignature`'s result from
`option` to a `Result<RepSigT, position * cause>` is a local change that hands
back everything the diagnostic needs.

### Family E — the typed side would **ACCEPT** (1 file)

051. **[MEASURED]** — `bad|C4 = confirm`, and the seam named `bad` as the
offender, so this is a genuine divergence, not a companion-function artifact.

```blade
function bad(x: Array<Float like PgIrrepsIdx<C4, EC>>, …) where ml.equiv(C4) -> … = {
    let sh = ml.y_to(1, a, b, c)          // O(3) op — and DEAD
    ml.derive_pg_linear(C4, EC, EC, x, w)  // this is what the body returns
}
```

The seam refuses `ml.y_to` **by name, wherever it appears** in a point-group
body. The typed walker flattens bindings and judges what reaches the result; an
unused binding contributes nothing, so it confirms.

**[ARGUED]** This is not a bug on either side — it is an unmade decision. The
seam's rule is syntactic and conservative ("this op has no C4 theorem, so it
does not belong in a C4 body"); the typed rule is semantic ("nothing
basis-dependent flows to the result"). A flip changes the answer, and the
question is which answer is wanted. The harness pins this case exactly, so a
*second* one, or this one changing shape, turns the block red.

### Family F — refused by a later stage anyway (5 files)

006, 009, 047, 052, 074. **[MEASURED]** — the shadowed program is refused by
the ordinary checker, before the typed walker gets a verdict:

| File | Seam says (BL4008) | Refused instead by |
|---|---|---|
| 006 | applying `exp` to a representation-typed value is not equivariant | BL3007 `exp applies to scalars` |
| 009 | body transforms as `IrrepsIdx<[(0,0,1),(1,1,1)]>` but the declared return is `IrrepsIdx<[(0,0,1)]>` | BL3001 type mismatch |
| 047 | `derive_pg_linear` names D4, this function is certified for C4 | BL4003 block-spec index mismatch |
| 052 | call to `d4_stage`: certified for D4, this function for C4 | BL4003 block-spec index mismatch |
| 074 | the outer-product form raises the rank of its result | BL3001 type mismatch |

**[ARGUED]** These are the flip's *free* wins on soundness and its *worst*
regression on message quality. The `(rejects)` probes stay green; the user goes
from "this operation is not equivariant, here is what to use instead" to a raw
type mismatch. Two of them (047, 052) are the cross-group certificate errors,
where the seam's message is the one that explains that certificates do not
transfer between groups — arguably the most educational message in the whole
channel.

### Summary table

| Family | Files | Typed verdict | Would the flip still refuse? |
|---|---|---|---|
| A engine refutes | 9 | DISAGREE | yes, different message |
| B composition refuses, cause discarded | 7 | ABSTAIN (`TBottom`) | **no** |
| C ml vocabulary gone by typecheck | 6 | ABSTAIN (`TOpaque`) | **no** |
| D signature-level refusal | 2 | ABSTAIN (classifier) | **no** |
| E dead-binding tolerance | 1 | **CONFIRM** | **no** |
| F later stage catches it | 5 | (unreachable) | yes, different code |
| | **30** | | **14 yes / 16 no** |

---

## 4. What a flip would do to `tests/corpus/ml-equiv`

**[MEASURED]** The category contains **zero** message pins: no `// ERROR:`, no
`// ERROR-CONTAINS:`, no `// REJECT-AT:`, and no `// EXPECT:` line on any
`(rejects)` file. The entire assertion is the `(rejects)` suffix on the
`// TEST:` name, which `tests/Runner.fs` reads as "the compiler must refuse
this".

So the flip costs **no pin rewrites here** — but 16 of the 30 equiv
reject-probes would flip from PASS to FAIL, because their programs would
compile. (Families B, C, D, E.)

The category's 23 `// SUGGEST:` pins are on the *suggestion* channel and are
already gated by `Test_RepDifferential.fs`; they are unaffected by rejection
parity.

---

## 5. Message parity, pin by pin

The worded pins live in `tests/corpus/diagnostics`. **[MEASURED]** 14 files pin
BL4008, carrying **37** `ERROR-CONTAINS` substrings and **13** `ERROR: BL4008 @
line:col` span pins. (A further 3 files pin BL4009 and 3 pin BL4012, with 7
more substrings — the other two disciplines, out of scope.)

The harness runs the same shadow rewrite over these files and asks, for each
pinned substring, whether *any* string the typed validation produces contains
it. That is a deliberately **optimistic** bound — it accepts a match from any
function in the file, not just the offender.

| File | substrings surviving | span pin surviving |
|---|---|---|
| 014 equiv hadamard | 0/1 | 1/1 |
| 015 equiv raw index | 0/1 | 0/1 |
| 016 equiv escape | 0/1 | 0/1 |
| 017 equiv gated parity | 0/1 | 0/1 |
| 021 engine generator check | 0/3 | 1/1 |
| 022 engine near miss | 0/4 | 1/1 |
| 023 engine constant | 0/3 | 1/1 |
| 024 engine cap | 0/4 | 0/1 |
| 025 engine Lie generator | 0/5 | 1/1 |
| 026 engine Lie near miss | **1**/6 | 1/1 |
| 027 engine inversion | **1**/5 | 1/1 |
| 037 equiv write index | 0/1 | 0/1 |
| 039 equiv former source | 0/1 | 0/1 |
| 044 equiv unclassifiable escape | 0/1 | — (no span pin) |
| **total** | **2 / 37** | **7 / 13** |

The two survivors **[MEASURED]** are `*sqrt(3)` (026) and
`that monomial is odd under -I` (027) — both incidental, both from the radical
renderer and the parity wording that `MLPolyExtractTyped` happens to share.

### Why the numbers are what they are

**Composition pins (014, 015, 016, 017, 024, 037, 039, 044 — 13 substrings):
[MEASURED]** the typed side produces *no text at all* at these sites. Its
answer is `walker declined (outside the composition fragment)` or
`nothing established for the body`. There is nothing to reword; the text has
to be **written**, at 25 `TBottom` sites, against 33 non-engine seam messages.

**Engine pins (021, 022, 023, 025, 026, 027 — 24 substrings): [MEASURED]** the
typed side produces text, but a deliberately abbreviated internal form. C2's
`MLPolyExtractTyped` says so in a comment: *"Deliberately SHORTER than
`MLEquiv.Engine.failureMessage`, which is the user-facing text and stays the
seam's: this string is for a compiler-internal disagreement report, and
duplicating the long form here would guarantee the two drift."* The divergence
starts at the first clause — the seam writes `the body IS a polynomial, and it
is not C4-equivariant`, the typed form writes the same words **without the
comma** — so even the pin that looks shared fails.

Beyond punctuation, four whole note-blocks exist only on the seam side and have
no typed counterpart: the **near-miss note** (the truncated-decimal trap and
both escape hatches, 6 pinned substrings across 022/026), the **constant note**
(3 substrings, 023), the **residual note** for Lie failures (1 substring, 025),
and the **cap note** (4 substrings, 024, which is also composition's verdict
being surfaced).

**Span pins: [MEASURED]** 7 of 13 "survive", but not by design. The production
site records a disagreement at `tBody.Span` —
`RepCheckDisagreements.add funcDecl.Name detail tBody.Span` — i.e. at the whole
function body, always. It coincides with the seam's span exactly when the body
is a single expression, which is true for 7 of these 13 files. The typed walker
threads no spans at all: `statusOf` has no span parameter, `TBottom` carries
none, and `RepCtx` has no field for one.

### The cost, stated plainly [ARGUED]

Three separate build-outs, in increasing size:

1. **Port the long-form engine messages.** `MLEquiv.Engine.failureMessage`,
   `lieFailureMessage`, `inversionFailureMessage` and `capNote` are pure
   functions of `PX.DischargeFailure` / `LD.LieFailure` /
   `LD.InversionFailure`, all of which `MLPolyExtractTyped` already has in
   hand. Moving them to a shared module recovers **24 of the 37 substrings**
   and is a refactor, not a design change. C2 declined to *duplicate* them, and
   was right to; *sharing* them is the correct move and removes the drift risk
   at the same time.
2. **Give `TBottom` a cause.** 25 sites, against 33 seam messages. Recovers the
   remaining 13 substrings, and is the same change that closes family B.
3. **Give the walker spans.** Needed for all 13 span pins to be *designed*
   rather than coincidental, and for any of these diagnostics to point at the
   offending expression rather than the whole body.

---

## 6. What the lattice would need

**[ARGUED]**, but grounded in the measured shapes above.

The brief's crux is confirmed: `TBottom` today means *"I decline to judge"* and
deliberately conflates "I cannot analyze this" with "this is wrong". A checking
authority must split them. The minimum split the measurements justify:

```
TBottom of cause: RefusalCause option   // None = no rule fired; Some c = a rule REFUSED
```

with `RefusalCause` carrying a message and a span. Families B and D need
nothing more than that plus `classifySignature` returning *which position*
failed. Family C needs it too, but is gated on provenance in the A1 stamp.

The verdict type then gains a fourth outcome beside `RepConfirm` /
`RepAbstain` / `RepDisagree`:

- **`RepRefuse of cause`** — a rule refused, the program is wrong, here is why
  and where. Distinct from `RepDisagree`, which is and must remain the
  *compiler-bug* signal (two proofs of one theorem contradicting).

Note the asymmetry that makes this safe: adding a cause to `TBottom` changes
nothing about deduction, because deduction already treats every `TBottom` as
silence. The information is currently computed and thrown away.

---

## 7. Recommendation

**[ARGUED] The flip is reachable, but not as a flip. Recommended end state is a
HYBRID, reached in four stages, with two families staying at the seam
permanently.**

### Stage C3-0 — instrumentation (prerequisite, small)

Add a compiler-internal shadow mode that demotes the seam's BL4008 to a note so
the typed side gets a look at refused programs. Without it every future
measurement repeats this document's source-rewrite trick, and `§1`'s shared
conjunct key means there is no other way. This block's `shadowEquiv` is the
throwaway version of it.

### Stage C3-1 — messages before authority (medium, no behaviour change)

1. Move `Engine.failureMessage` / `lieFailureMessage` /
   `inversionFailureMessage` / `capNote` into a module both `MLEquiv` and
   `MLPolyExtractTyped` consume. **Recovers 24 of 37 substrings; removes the
   drift risk C2 correctly flagged.**
2. Widen `classifySignature` to report the failing position and cause.
   **Closes family D (2 files) end to end.**
3. Thread spans through `statusOf` and give `TBottom` a cause. **Closes family
   B (7 files) and the remaining 13 substrings.**

None of this changes any verdict. The existing gates (`rep-check`,
`rep-differential`) and this census stay green throughout, and after step 3 the
census's family-B and family-D rows can be re-measured as `DISAGREE`.

### Stage C3-2 — provenance for the generated decls (medium)

Extend the A1 elaborator stamp with the originating `ml.*` op name and span, so
a rep/invariant mismatch at a call to `__ml_N` can be reported against
`ml.derive_sym_tp(...)`. **Closes family C (6 files).** This is genuinely new
data, not plumbing, and is the one stage that could slip.

### Stage C3-3 — the decision, then the flip (small, but blocking)

Family E (051) is a semantic question a measurement cannot answer: does a
**dead** binding of an O(3) value inside a point-group body stay refused? The
seam says yes (syntactic, by op name); the typed walker says no (only what
reaches the result matters). Pick one, write it down, and pin it. Then flip.

### What stays at the seam permanently [ARGUED]

- **Family F's better messages.** After a flip 006/009/047/052/074 are refused
  by the ordinary type checker, correctly but unhelpfully. The equivariance
  *explanation* for these — especially the two cross-group ones — has no home
  in a type error. Recommendation: keep a seam-resident **lint** that runs
  before the type checker and emits these five messages as notes, without
  authority to refuse. Cheap, and it is the only way the user keeps the
  sentence that teaches them why certificates do not transfer between groups.
- **The galilean and perm disciplines** (15 of the 47 probes) are not part of
  this gate at all. Each needs its own census before C3's other half is
  scheduled. Recommendation: do not fold them into the equiv flip.

### The honest alternative

**[ARGUED]** `plan-equivariance-in-types.md` §5.3 already asks whether
"B-forever" — deduction typed, checking at the seam — is a stable end state. On
this measurement it is a defensible one: the seam is 1820 lines carrying 37 BL4008
construction sites, each with its own wording and an accurate sub-expression
span; the typed side would need
stages C3-1 and C3-2 in full just to *match* that, and would gain, concretely,
family A's better reach (which it already has, as a second opinion) and the
partial-annotation recall the differential already banks without any flip. The
case for flipping is architectural (one engine, three disciplines, per plan
§2), not diagnostic. That case is real, but this census says it should be
argued on its own terms and not on a claim that the typed side is ready to
refuse programs — it is not, on 16 of 30 measured cases.

---

## 8. Reproducing

```
dotnet build Blade.fsproj -c Release
dotnet bin/Release/net7.0/Blade.dll test rep-reject
```

The block prints, per file: the seam's channel, the functions it named, its
first message, and the typed verdict for every certificate in the file (with
`*` marking the seam's offenders). It asserts only its own health plus the two
alarming directions; every census number is a `[SKIP]` line, so a change in the
corpus moves the numbers without turning the block red for the wrong reason.

Known pins in the block that will need updating if behaviour changes:

- `knownPermissive` — the single accepted typed CONFIRM (051). A second one
  fails the block.
- Obligation 1's scoping to the three ml-elaboration channels, which encodes
  §1's structural fact.

Residue: the block registers a constraint handler named
`__rep_reject_census_shadow` in the process-wide `Blade.Constraints` registry,
which has no unregister. Safe — no test pins the registered-vocabulary list,
and the spelling is unwriteable by accident.
