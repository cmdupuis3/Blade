# Census: where should the PERM discipline check and deduce?

Measured 2026-07-29 on branch `p1-perm`, at `18b1f06` + this branch's harness.
Instrument: `tests/Test_PermLayerCensus.fs`, verb `blade test perm-layer`. Every
number below is reproduced by that block on every run; nothing is estimated.

**Scope.** This answers, for PERM alone, the question
`docs/design-discipline-as-data.md` poses for all three equivariance-family
disciplines, and it is the third and last of the three. It follows the decision
that the disciplines are NOT unified: `DisciplineKit` is a library perm may call,
not a framework it must instantiate, and **perm inherits neither equiv's answer
nor galilean's**. Nothing here changes any checking behaviour, moves any gate, or
touches `src/ml/compiler/MLPerm.fs`.

**Claim labelling.** Every claim is **[M]** (produced by the harness, by a probe
run against the compiled binary, or by reading the code path it exercises) or
**[A]** (a judgement about what the measurement implies).

**Honesty about the instrument.** The typed perm judgment used here is
EXPERIMENTAL and lives in the test assembly. Nothing in `src/` references it; it
emits no diagnostic; it is not in the full suite. It is a measuring instrument,
not a shipped discipline.

---

## 0. Headline

| Question | Answer |
|---|---|
| Perm certificates in the corpus (source pins) | **17** [M] |
| …in files the seam ACCEPTS | 5 (4 files) |
| …in files the seam REFUSES | 12 (11 files) |
| **Acceptance census**, port-as-is | **0 confirm / 5 abstain / 0 disagree** [M] |
| **Acceptance census**, with the generated ops recognized | **5 confirm / 0 abstain / 0 disagree** [M] |
| **Rejection census**: perm-channel reject files | **11** [M] |
| …offender verdicts where the typed side would **still refuse** | **0 of 11** |
| **Inference** A — no op recognition, N enumerated | **0 proposals** [M] |
| **Inference** B — op recognition, N enumerated | **5 proposals, recall 5/5, 0 extra** [M] |
| **Inference** C — op recognition, N GIVEN (the `__nodepow` tag) | **identical to B** [M] |
| Proposals refused by the shipped seam when pinned back | **0 of 10** [M] |
| Pins the seam REFUSES that inference re-derives | **0 of 12** [M] |
| `tests/corpus/diagnostics` BL4012 `ERROR-CONTAINS` pins surviving a flip | **0 of 3** [M] |
| BL4012 span pins surviving | **0 of 3** [M] |
| Legal `derive_perm_linear(K,L,N)` configurations blocked by the extent caveat | **14** at N ≤ 64 [M] |

**The verdict, in one line [A]:** *perm's checking should stay at the seam and
its deduction should be BUILT AT THE SEAM — the first discipline for which the
layer question is not the interesting one, because the deferral that has been
blocking it (`§0.2`, "perm inference becomes feasible at typecheck") turns out
not to be a layer problem at all.*

**The three findings that decide it:**

1. **The typed walker cannot confirm a single perm certificate.** [M] Not one of
   the five — 0 confirm / 5 abstain. Perm's `Pow k` for k ≥ 1 can only be
   produced or consumed by three ops (`ml.derive_perm_linear`,
   `ml.derive_perm_bias`, `ml.perm_matmul`), and by typecheck those are anonymous
   `__ml_<n>` calls carrying no stamp. This is equiv's family C — "the `ml`
   vocabulary is gone by typecheck" — but **total** rather than 6 of 30.

2. **§0.2's premise is wrong twice over, and the second way is new.** The
   `design-discipline-as-data.md` §5.3 correction (a flat extent factors many
   ways) is independently confirmed here by probe (a). But probe (b) finds a
   more basic problem the correction did not name: **a `let static`-sized extent
   is not a literal at typecheck at all.** `Idx<W1>` arrives as
   `IRParam ("W1", 0, IRTNat None)`; statics are substituted in Lowering's Phase
   0, *after* typecheck. The typed walker can resolve it only by being handed the
   same static environment the seam already carries — which is itself produced by
   the seam's own alias rewrite.

3. **The `__nodepow` tag buys nothing measurable for inference.** [M]
   Configurations B and C are byte-identical: 5 proposals, recall 5/5, zero
   proposals beyond the pins, zero refused by the seam. Enumerating N over the
   integer roots of the signature's extents explores 1–3 candidates per function
   and produces **no noise at all** on this corpus. The tag's real prize is
   somewhere else entirely — see §7.

---

## 1. The incumbent, characterized

### 1.1 The five axes

**[M]** by reading `src/ml/compiler/MLPerm.fs` (940 lines) and
`src/ml/compiler/MLPermSpec.fs` (301).

| axis | perm |
|---|---|
| **Status lattice** | `Pow of int \| PowUnsized \| POpaque`, plus `Error` in a `Result` (`fs:151-167`). ℕ-graded. `Pow 0` is the invariant status and `Pow k` (k ≥ 1) the covariant one — one constructor, two roles, split by guard at every arm. `PowUnsized` is a THIRD level: invariant-SHAPED but of unestablished extent, which is *not* a claim of fixedness (`fs:156-166`). |
| **Classifier** | The FLAT EXTENT of a single `Idx<>` axis, **relative to N**: `Array<_ like Idx<M>>` is `Pow k` iff M = N^k (`fs:239-260`, `powClass` at `:201-206`). Rank ≥ 2 arrays and non-`Idx` axes are a HARD REJECT before any body is walked (`:257-260`, `:271-272`). Fully-annotated signature required (`:328-329`). |
| **Transfer rules** | A PERMUTATION MATRIX — monomial, 0/1. It moves cells without mixing them, so it commutes with *every* pointwise map: `+ - * /` all preserve the rank (`fs:581-582`), scalar broadcast is legal, and pointwise nonlinearities are legal (`fs:457-458`). Exactly what equiv forbids. |
| **Claim vocabulary** | `__ml_perm_equiv(N)`, a `WhereClause.Custom` conjunct read by the shared `MLCertShell.conjunctsOf`. Errors BL4012. **No suggestion channel, no structured fact, nothing in `Ide.fs`** — verified [M]: `grep perm src/Ide.fs` is empty, and `CertFacts` carries only `"equiv"` and `"galilean"`. |
| **Mode** | `import ml` **and** a non-empty cert table (`MLElaborate.fs:2160` short-circuits on `Map.isEmpty pcerts`). Hypothesis space: **none — no inference exists.** |

### 1.2 The three things that make perm structurally different

**(a) The classifier is not a function of the type.** [M], probe (a):

```blade
function f(x: Array<Float like Idx<16>>, c: Array<Float like Idx<2>>)
           where ml.perm_equiv(4) -> Array<Float like Idx<16>> = x * c   // OK
function f(x: Array<Float like Idx<16>>, c: Array<Float like Idx<2>>)
           where ml.perm_equiv(2) -> Array<Float like Idx<16>> = x * c   // BL4012
```

Byte-identical parameter types; one differing integer in the conjunct; opposite
seam verdicts. At N = 4 the 16-cell axis is `Pow 2` and the 2-cell axis is not a
node power at all (`Pow 0`, a legal broadcast); at N = 2 they are `Pow 4` and
`Pow 1`, a rank mix. **So `IRType -> status` is not merely the equiv-shaped
special case (`design-discipline-as-data.md` §4.2) — for perm it is not even
well-defined without N.** The classifier is `(N, IRType) -> status`, which is the
signature-level lifting again, arrived at from a third direction.

**(b) The evidence is the OP VOCABULARY, and it is seam-only.** [M]
`MLElaborate.derivePermLinearDecl` (`fs:1163-1181`) emits

```
function __ml_<n>(x: Array<Float like Idx<N^K>>, w: Array<Float like Idx<W>>)
    -> Array<Float like Idx<N^L>>
```

via `mkFunc`, which sets `WhereClause = None` (`fs:114-121`), and MLElaborate's
stamping block declines to stamp the Sₙ layers **by name** (`fs:176-180`:
"the Sₙ index-action layers, whose discipline is `__ml_perm_equiv`, not this
one"). Nothing records (K, L, N). At typecheck the op is an uncertified callee
with a moving argument, which the kit's call rule declines — hence 0 confirm.

**(c) Perm touches STORAGE, and the two other disciplines never do.** See §6.

---

## 2. The instrument, and its calibration

`Blade.ML.Elaborate.expand` runs **before** `checkProgram`, so a seam BL4012
makes `typeCheck` return `Error` and the typed walker never sees the program.
**[M]**, obligation 1: on all 11 perm-channel reject files, no certificate
reaches typecheck at all.

The way through is R1's, reused in method: rewrite `ml.perm_equiv(` to a
test-registered inert conjunct. `MLPerm.buildCertTable` then returns an EMPTY
table, which `MLElaborate` short-circuits (`fs:2160`, `Ok pcerts when
Map.isEmpty pcerts -> Ok ()`), so the seam falls silent and the program reaches
typecheck still carrying N in the shadowed conjunct.

**Calibration** [M], obligation 3: on every file the seam ACCEPTS, the typed
verdicts computed from the SHADOWED source equal those computed from the
UNSHADOWED source, function for function. 4 files, all agree.

**Two instrument artifacts, both isolated and both fixed rather than reported as
findings:**

1. The static environment must be rebuilt from the **shadowed** source. On a
   seam-rejected file `expand` returns `Error`, the alias rewrite never happens,
   and every weight buffer would read as unclassifiable — which would have shown
   up as a spurious "signature does not classify" family.
2. `staticIntsOf` runs `ML.Elaborate.expand` before `resolveStatics`, because
   `ml.perm_weight_dim` is registered as `__ml_stat_perm_weight_dim`
   (`MLStatics.statName`) and only the seam's alias rewrite produces that name.
   **This is faithful — typecheck does the same — but it is worth stating that
   the typed layer's access to those numbers is INHERITED FROM THE SEAM.**

---

## 3. The acceptance census — the number that decides it

**[M]**, `blade test perm-layer`. 5 certificates over 4 files.

| | certs | confirm | abstain | disagree |
|---|---|---|---|---|
| port as-is (no op recognition) | 5 | **0** | **5** | 0 |
| with the generated ops recognized | 5 | **5** | 0 | 0 |

All five abstentions carry one reason: `walker declined (outside the perm
fragment)`, and the cause is the same in every case — the body reaches
`ml.derive_perm_linear` / `ml.derive_perm_bias` / `ml.perm_matmul`, which by
typecheck is an uncertified `__ml_<n>` callee receiving a `Pow k` argument.

**This is the sharpest single result in the three censuses.** Galilean's typed
walker confirms 19 of 23 with no help at all; equiv's confirms 54 of 130. Perm's
confirms **zero**, and then **all five** once one integer triple is supplied per
generated decl. There is no middle.

### 3.1 The recognizer, and why it is a price tag rather than a proposal

The harness closes the gap with a RECOGNIZER (§4 of the block): given a candidate
N, an `__ml_`-prefixed decl's extent triple determines (K, L) uniquely — N ≥ 2
makes the powers strictly increasing — and the weight-buffer extent is then a
Bell-number CHECK rather than a further unknown. **[M]** it recognizes every
generated perm op in the corpus and nothing else.

**[A] That the recognizer WORKS is a finding; that it should be BUILT is not.**
It is precisely R1's objection to equiv's family-C remedy: the typed checker
would be judging a *reconstruction* of the surface program. The right shape is
the one stage A1 already uses for equiv — the elaborator STAMPS what it knows.
The recognizer exists here to price that stamp, and the price it reports is:
**one stamp turns 0 confirm into 5 confirm, and 0 inference proposals into 5.**

---

## 4. The rejection census

**[M]**, 11 perm-channel reject files, 12 offender verdicts.

| Family | Files | Typed verdict | Would a flip still refuse? |
|---|---|---|---|
| **B** — a rule refused, and the cause is discarded | 10 | ABSTAIN (`PBot`) | **no** |
| **D** — the refusal is in the SIGNATURE | 1 | ABSTAIN | **no** |
| | **11** | | **0 yes / 11 no** |

**There is no family A.** [M] Equiv's and galilean's censuses each have one: a
body the typed walker judges to a DEFINITE status that contradicts the
declaration, which is the only rejection shape a lattice can express. Perm has
none, and the reason is structural: **without the ops, the walker never reaches a
definite non-zero rank at all**, so it can never contradict anything. A perm
`PDisagree` requires the very stamp §3.1 prices.

**Family D is different from equiv's.** Corpus 043 (`Array<Float like Idx<4>,
Idx<4>>` under a perm certificate) is refused by the typed classifier too, with
the same cause — a rank-2 array needs one status per AXIS. But the typed verdict
is ABSTAIN rather than a refusal, because `classifySignature` returning `None` is
an absence, not an error. This is R1's family D verbatim, one discipline over.

**Two rejections deserve naming individually:**

* **042 (wrong N).** The seam refuses `ml.derive_perm_linear(1, 1, 3, x, w)`
  inside a `perm_equiv(4)` body. **[M] the shadowed program TYPECHECKS**, even
  though `x : Idx<4>` is passed to a generated parameter of type `Idx<3>` —
  Blade's unifier does not compare extents. So this rejection has **no
  typecheck-side backstop of any kind**: a flip would let it compile, and the
  type system would not catch it either.
* **038 (opaque index).** `acc(s.i) = 1.0` where `s.i` is a struct field. An
  early version of the typed instance CONFIRMED this program, because the kit's
  field arm answers `FixTop` for a fixed base and an earlier index rule accepted
  anything invariant-shaped. Tightening the index rule to `Pow 0` EXACTLY — the
  polarity `MLPerm.judgeAssign` uses, with three separate messages at `fs:719-731`
  — turns it back into an abstain. **[A] Recorded because it is the exact shape
  of a false accept, found by the census rather than by a user.**

---

## 5. Message parity

**[M]** `tests/corpus/diagnostics` pins BL4012 in **3 files**, carrying **3
`ERROR-CONTAINS` substrings** and **3 span pins** (`035 @ 6:22`, `036 @ 6:7`,
`038 @ 7:7`).

**All 3 substrings and all 3 span pins die under a flip.** The typed side
produces `walker declined (outside the perm fragment)` at all three sites, and
`PBot` is a nullary constructor threading no span.

`tests/corpus/ml-equiv` pins no BL4012 message text; its reject-probes assert
only the `(rejects)` suffix.

**[M] `MLPerm.fs` constructs 41 distinct BL4012 messages** — 20 direct `bl4012`
applications minus the 4 that are `reject`/`fail` helper DEFINITIONS (`fs:314,
479, 697, 748`), plus 19 `reject` and 6 `fail` applications — **of which 38 are
inside the judgment proper.** For comparison: galilean has 37, equiv's census
counts 37 pins and 13 spans.

**[A]** So the pin-rewriting cost of a flip is small (3 + 3) and the
message-WRITING cost is large (38), which is galilean's shape. The difference is
that for galilean the messages were the *only* thing standing in the way; for
perm they are the second thing, behind an evidence loss that is total.

---

## 6. Storage — the interaction the other two disciplines do not have

This is the section with no analogue in the equiv or galilean censuses, and it is
the most interesting thing in the file.

### 6.1 Perm's flat buffers and Blade's compact storage are the SAME AXIS

`ml.perm_equiv` keys on a flat row-major `Idx<N^k>` buffer. Blade's compact
symmetric storage — `SymIdx<r, n>`, where `comm`/`anticomm` live — stores the
same mathematical spaces at their *combinatorial* cardinality. A symmetric N×N
node-pair matrix is a perfectly good Sₙ-module (conjugation by a permutation
matrix preserves symmetry), and it is exactly what `SymIdx<2, N>` stores.

### 6.2 v1 refuses it, and the refusal is load-bearing

**[M]**, probe (c):

```blade
function s(x: Array<Float like SymIdx<2, 4>>)
           where ml.perm_equiv(4) -> Array<Float like SymIdx<2, 4>> = x + x
```
> `error[BL4012]: function 's', parameter 'x': only plain `Idx<>` axes are
> classified in a perm-certified signature.`

That refusal is by SURFACE SYNTAX (`MLPerm.statusOfType`'s `TyNamed` arm). **At
typecheck the syntax is gone**: a `SymIdx<2, 4>` axis is an index type with
`Rank = 2`, `Symmetry = SymSymmetric`, `Extent = 4`. **[M]** an extent-only typed
classifier reads it as `Pow 1` — a node VECTOR — for a buffer that is a node
PAIR-matrix. That is a wrong status in the covariant direction, and it would let
the buffer satisfy a `derive_perm_linear` K = 1 input slot.

**[A] So the typed port must reconstruct, from `ix.Rank` / `ix.Symmetry` /
`ix.IxKind`, a guard the seam gets free from the source text.** The harness's
classifier does exactly that (`classifyPermTyR`), and probe (c) pins the
difference so the guard cannot be dropped silently. This is a third instance of
the same pattern as §1.2(b) and §2: **at every point where perm's judgment leans
on something, that something is more available at the seam.**

### 6.3 The non-power arm, and why `PowUnsized` exists

**[M]** the same 10-cell space spelled as a flat dense axis compiles:

```blade
function s(x: Array<Float like Idx<10>>)
           where ml.perm_equiv(4) -> Array<Float like Idx<10>> = x + x   // OK
```

and 10 is not a power of 4, so the classifier calls it **invariant**. For a
symmetric node-pair buffer that is false — a relabelling moves it.

**This is sound as written, and the reason is worth stating precisely.** A
certificate is a CONDITIONAL theorem about its parameters ("IF this parameter
transforms as declared THEN…"), so a caller who passes a moving 10-cell buffer
has simply falsified the hypothesis. What the reading does *not* license is the
walker manufacturing that claim about a value it builds INSIDE a body — and that
is exactly what `PowUnsized` (`MLPerm.fs:156-166`) exists to prevent. Both the
non-`Idx` refusal and `PowUnsized` are load-bearing, and a port that keeps one
without the other is unsound.

### 6.4 The v2 that unifies them [A]

Per-axis status vectors (`MLPerm.fs:94-99`) are the named v2 for perm, and
`SymIdx` is the shipped storage for exactly the case they describe — a rank-r
array whose axes are interchangeable. **The interesting observation is that
they are the same feature seen from two sides:** a perm status vector that reads
`[Pow 1; Pow 1]` on a rank-2 array whose two axes are the SAME node axis is
precisely the condition under which `comm` licenses the compact `SymIdx<2, N>`
retyping (`plan-equivariance-in-types.md` review decision 1: "a pin IS a
POLYMORPHISM LICENSE"). Perm v2 and symmetric storage want the same axis-level
bookkeeping, and nobody has connected them. That is a design note, not a
recommendation, and it is out of scope for this census.

---

## 7. Inference — the capability prize, and the tag's price

### 7.1 The measurement

**[M]** Three configurations, over all 15 perm files, with every proposal gated
by writing the pin back and running the SHIPPED SEAM CHECKER.

| config | proposals | recall (of 5 certified pins) | beyond the pins | survived the seam gate | refused pins re-derived (of 12) |
|---|---|---|---|---|---|
| **A** no op recognition, N enumerated | 0 | 0/5 | 0 | — | 0 |
| **B** op recognition, N enumerated | 5 | **5/5** | **0** | **5/5** | 0 |
| **C** op recognition, N GIVEN (the tag) | 5 | **5/5** | **0** | **5/5** | 0 |

The gate is the only half of a differential perm can have, and it is real:
**[M]** a NEGATIVE CONTROL pins `shuffle` (the escape probe's genuinely
non-equivariant helper, which reads `z(0)` out of its node axis) and the gate
correctly reports BL4012.

The deduction twin is `MLGalilean.inferGalileanCertificates`'s shape transplanted
onto perm's hypothesis: decl order, one speculative table, self-reference refused
(`Self = tf.FuncId`), a **vacuity guard** (a signature whose every parameter
classifies `Pow 0` says nothing about relabelling and is suppressed — without it
every scalar function "passes" at every N), and the proposal rule "the derived
body status is DEFINITE and equals the declared return's".

### 7.2 What enumerating N actually costs [M]

Per function, the candidate set is every integer k-th root of every extent in the
signature:

| function | candidates |
|---|---|
| `readout` (039, 077) | {2, 4} |
| `ppgn` (040) | {3, 9} |
| `ppgn_trace` (040) | {2, 3, 9} |
| `drift` (041) | {3} |
| `third` (077) | {3} |

1–3 candidates, and **exactly one survives in every case**. The `N` the human
wrote is re-derived, and no other N produces a proposal. MLEquiv's worry
(`fs:1605-1607`, "guessing N from an `Array<_ like Idx<n>>` would propose noise")
is **not borne out on this corpus** — because a wrong N makes the *ops* stop
recognizing, and the ops are the only thing that can produce a definite rank.

### 7.3 The tag verdict [A]

**Do not build `__nodepow` for inference. It buys nothing measurable, and the
thing it was proposed to unblock is unblocked by something far cheaper.**

*What it would cost.* By the `IxKPgIrreps` precedent (`Types.fs:113-117`,
"twin, not reroute"): a new `IxKNodePow` kind, a frozen parameterized tag format
`__nodepow:<N>:<k>` with a `mkNodePowTag` / `(|NodePowTag|_|)` pair, an entry in
`ixKindSentinel`, a surface spelling, a nominal arm in `Unify.fs` beside the
`lat ≠ lon` discipline (`fs:519`), the IR validator's Tag/IxKind agreement check,
`checkArrayIndexTags` interop, gradual-adoption rules against plain `Idx<M>`
(what happens when a tagged axis meets an untagged one of the same extent), and
emitter changes in `derivePermLinearDecl` / `derivePermBiasDecl` /
`permMatmulDecl` so the generated signatures carry it. **It is a language change,
not a checker change** — `design-discipline-as-data.md` §8 rates track P1 HIGH
risk, and that rating stands.

*What it would buy, measured:* zero recall, zero precision, and a reduction in
search from 1–3 walks per function to 1.

*What it would buy that is REAL, and this is the one honest argument for it* —
**[M]**, probe (d):

```blade
let static W = ml.perm_weight_dim(1, 1, 2)     // = Bell(2) = 2
function layer(x: Array<Float like Idx<2>>, w: Array<Float like Idx<W>>)
               where ml.perm_equiv(2) -> Array<Float like Idx<2>> =
    ml.derive_perm_linear(1, 1, 2, x, w)
```
> `error[BL4012]: derive_perm_linear weight buffer must be invariant (Pow 0),
> but the argument is node-covariant of rank 1`

**The smallest DeepSets layer at the smallest legal node count is UNWRITABLE**,
because its own weight buffer has 2 slots and 2 = 2¹. The identical layer at
N = 4 compiles. **[M] exactly 14 of the legal `(K, L, N)` configurations at
N ≤ 64 are blocked this way**, and every one of them collides at N¹ — the weight
count equals N exactly:

| K + L | Bell(K+L) | blocked at N = | configurations |
|---|---|---|---|
| 2 | 2 | 2 | (1,1), (2,0) |
| 3 | 5 | 5 | (1,2), (2,1), (3,0) |
| 4 | 15 | 15 | (1,3), (2,2), (3,1), (4,0) |
| 5 | 52 | 52 | (1,4), (2,3), (3,2), (4,1), (5,0) |

i.e. the whole diagonal `N = Bell(K+L)`. (K + L = 6 collides at N = 203, past
the sweep's window but inside the surface's own cap.)

That is the coincidental-extent caveat (`MLPerm.fs:101-108`) biting a program a
user would obviously write, and **nominal keying is the only fix that works** —
the extent really is a node power, so no arithmetic can distinguish the weight
buffer from a node vector. But the affected N values are 2, 5, 15 and 52, of
which only N = 2 and N = 5 are plausible in practice, so **[A] the trigger for
building the tag is a user hitting that wall, not this census.**

*The cheap mitigation, worth doing either way [A]:* `elabDerivePermLinear` can
detect that `permWeightDim K L N` is itself a power of N and emit a dedicated
diagnostic naming the caveat, instead of leaving the user with a confusing
message about a buffer whose size they did not choose. One arm in the elaborator,
beside `checkPermSizing`'s existing no-silent-fork refusals.

---

## 8. Can perm use `DisciplineKit`?

### 8.1 v1: YES, with two recorded mismatches, neither of which costs anything today

**[M]** The typed instance consumes `DisciplineKit.structuralArm` unchanged for
vars, if, match, let, block, sequence, assign, tuple-index, field, compute,
lambda, **the 104-line interprocedural call rule**, and the former-application
walk. Perm supplies its own `ruleArm` for exactly the node kinds the kit declares
as the rules' (literals, arithmetic, unary/negate/conjugate, indexing, reduction,
aggregates, virtual arrays) — the abstraction boundary holds.

**Mismatch 1 — `StatusOps` offers ONE `IsFix` predicate and perm needs two
polarities.** The seam requires an `if` condition / `match` scrutinee to be
`Pow 0` EXACTLY (`fs:593, 602`), and permits an argument to an uncertified callee
to be `Pow 0` OR `PowUnsized` (`fs:844`). Both are runnable in the harness.
**[M] NO corpus certificate changes verdict between them** — the mismatch is real
in the rules and latent in the corpus. The instance ships the strict reading,
which is the conservative direction (costs recall, never soundness).

**Mismatch 2 — `StructRules.FormerConclusion` receives `anyCovSrc: bool` but not
the source status LIST**, which MLPerm's extent claim wants (`fs:568-570`). The
typed side answers the same question a strictly better way — it reads the RESULT
node's own extent — so **[A]** the mismatch costs nothing here, and the reason it
costs nothing is a typed WIN rather than a lucky fit.

**One thing that FITS surprisingly well.** The node-axis op stamp is a fixed
`(params → return)` statement, which is exactly `CallSig`. Unlike galilean's
"preserves" claim (a *dependent* signature, which `CallSig.CReturn` cannot
express and which needed an arm of its own), **perm's missing stamp needs no kit
change at all.**

### 8.2 The one arm the move to typecheck forces, and it is not a kit problem

**[M] The single largest behavioural finding in building the instrument.** At the
seam, `MLPerm`'s former arm refuses ANY node-covariant source (`fs:549-553`),
because the kernel of a user-written `method_for(x) <@> …` receives COMPONENTS.
At typecheck **`h * h` on two arrays IS a former application** — the desugarer
produced it. Porting the seam's rule verbatim therefore refuses **perm's entire
pointwise fragment**, which is the discipline's whole polarity headline.
Measured: with the verbatim rule the acceptance census is 1 confirm / 4 abstain
even *with* op recognition; with the rule below it is 5 / 0.

The discriminator is the kit's own `isElementwiseArith`, handed in as
`FormerConclusion`'s third argument: a kernel inside the componentwise-uniform-
linear fragment applies one map cell-by-cell, and a permutation commutes with
every pointwise map, so the result is the pointwise combination of the sources'
statuses — synthesized or hand-written. A kernel outside that fragment is doing
real component work and is refused, as at the seam.

**[A]** Note the direction: the typed side ends up *more precise* than the seam
here (it can tell `method_for(x) <@> lambda(xi) -> xi * 2.0` really is pointwise,
where the seam refuses uniformly). Corpus 066 and 067 are still rejected, because
their result then fails the weight slot's `Pow 0` requirement — a different and
better reason.

### 8.3 v2: NO — and this confirms the prior survey's flag, with a line number

**[A], and the argument is specific rather than general.** `design-discipline-
as-data.md` §5.3 names perm v2's per-axis status vectors as the clearest live
case for bypassing `structuralArm`. That is right, and the reason is one arm:

`DisciplineKit.fs:576-578` binds a former's kernel parameters to the SOURCE
statuses **verbatim**, with an explicit comment that this is deliberate. For
per-axis vectors it is wrong, not merely imprecise: a former iterating a rank-2
array hands its kernel an ELEMENT, whose status vector is the source's *minus the
iterated axes*. **There is no hook to transform a source status into a
kernel-parameter status** — `StructRules` has two fields, and neither is it.
`CovAppliedAsCallee` has the twin problem: it receives the callee's status but
not the index positions, so it cannot project either.

So the honest v2 verdict is: **perm v2 should write its own walk and call only
the §5.2 anti-drift helpers** (`staticIntOf`, `isElementwiseArith`,
`mentionsAnyId`). Adding axis-awareness to `structuralArm` would rebuild the
framework the reframe deliberately removed — and it would do so for a shape the
other two disciplines do not want.

---

## 9. Recommendation

### 9.1 Checking → stays at the seam. Strongly, and for a third distinct reason.

**[A], on §3 and §4.**

Equiv's checking stays because 6 of its 30 rejections turn on a vocabulary that
is gone by typecheck. Galilean's stays because `GBottom` carries no cause or
span, which is a diagnostics problem. **Perm's stays because the typed walker
cannot confirm a single one of its five certificates** — a capability problem,
and the most severe of the three.

| | equiv | galilean | perm |
|---|---|---|---|
| typed CONFIRM rate on accepted certificates | 54/130 | 19/23 | **0/5** [M] |
| rejections surviving a flip | 14/30 | 1/11 | **0/11** [M] |
| worded diagnostic pins surviving | 0/37 | 0/3 | **0/3** [M] |
| a family A (definite contradiction) exists | yes | yes | **no** [M] |

Even with the op stamp — which turns 0/5 into 5/5 — **the rejection number does
not move**, because every perm rejection is a `PBot` and `PBot` carries neither a
cause nor a span. Perm would need BOTH the stamp AND the 38-message cause-and-
span work before the flip is even discussable. Nothing recommends starting.

### 9.2 Deduction → build it AT THE SEAM. This is the recommendation that matters.

**[A], and it is the finding this census exists to deliver.**

`plan-equivariance-in-types.md` §0.2 promised that moving to typecheck would
unblock perm inference, and §6.1 told readers not to expect it before stage C3.
**The measurement says the layer was never the obstacle.** What inference needs
is:

1. the walk — which the seam already has (`MLPerm.judge`, 190 lines, shipped);
2. a hypothesis space over N — which this census demonstrates works, with the
   corpus's own extents as candidates, zero noise, recall 5/5;
3. a vacuity guard — one line over the classified signature;
4. knowledge of the ops' (K, L, N) — **which the seam reads directly off the
   surface call and the typed layer can only get from a stamp or a recognizer.**

Item 4 is the whole difference, and it points the opposite way to §0.2. **[A]
Perm's inference is CHEAPER at the seam than at typecheck**, and the harness's
proposal engine ports there essentially unchanged (`inferAtN` is ~50 lines and
touches nothing typed except the classifier, which has a seam twin already).

What the seam version needs that this harness does not model:

* a suggestion channel — perm has NONE (§1.1). It needs a `PermCertSuggestions`
  beside `CertSuggestions`/`GalCertSuggestions`, a warning code (BL4011 is
  reserved, BL4014 is galilean's — perm needs a new one), and a `CertFacts` entry
  with `Discipline = "perm"` so `ide check --json`'s `deduced[]` carries it. The
  compile-order note applies (`CertFacts` lives in MLEquiv for that reason).
* the strongest-first vs every-passer decision. **[A]** Perm's candidates are
  competing *readings of the same buffers*, not independent claims, so equiv's
  FIRST-PASSER selection is the right precedent, not galilean's every-passer.
  Measured support: exactly one N passes per function in the corpus, so the
  choice is currently unobservable — which is the right time to make it
  deliberately.

*The one thing the typed layer would still add:* partial-annotation recall.
`MLPerm.buildCertTable` refuses an unannotated parameter outright (`fs:328-329`),
and at typecheck an unannotated parameter's type is closed by its call site. That
is a genuine gain and it is equiv's §0.1 payoff verbatim — but **no corpus file
exercises it**, and it is a recall refinement to schedule *after* the capability
exists, not a reason to build the capability somewhere harder.

### 9.3 The elaborator stamp — worth doing on its own merits [A]

Independent of any layer decision, `MLElaborate` should stamp its generated Sₙ
decls with their `(K, L, N)`. Today it explicitly declines (`fs:176-180`), and
the reason given is correct as far as it goes — `__ml_perm_equiv` asserts a
theorem about a *node axis*, and stamping the ops with it would be the wrong
claim. But that is an argument against stamping them with the WRONG conjunct, not
against recording what they are. The measured payoff is the whole of §3 and §7:
0→5 confirms and 0→5 proposals. It is the same shape as stage A1 for equiv, and
it is the only prerequisite any typed perm work would ever have.

### 9.4 Ordering, if any of this is scheduled

1. **Perm inference at the seam** (§9.2). The capability prize, cheapest path,
   proven algorithm, and it needs no stamp, no tag and no layer move. Gate: this
   block's obligation 5 shape — every proposal's pinned twin must CHECK under the
   seam — which is already implemented here and can be lifted verbatim.
2. **The suggestion channel** it needs (BL-code assignment, `CertFacts` kind,
   `deduced[]` plumbing). Small, and it is the reason a user would ever see the
   inference.
3. **The coincidental-extent diagnostic** (§7.3's cheap mitigation). One arm in
   `elabDerivePermLinear`. Turns a confusing message into an honest one.
4. **The elaborator stamp** (§9.3), whenever typed perm work is contemplated.
5. **`PBot` gains a cause and a span** — 38 sites. Worth doing on its own merits
   (it is what would let the seam and any second opinion agree on wording), and
   it is the entire cost of making perm's checking *discussable*.
6. **`__nodepow`** — only on the trigger in §7.3, and understood as a language
   change.
7. **Per-axis status vectors** (v2) — a separate feature, whose walk should NOT
   go through `structuralArm` (§8.3), and which is the same axis-level
   bookkeeping `SymIdx`/`comm` already do (§6.4).

### 9.5 Three answers, three reasons

| | equiv | galilean | perm |
|---|---|---|---|
| checking | seam — evidence gone | seam — diagnostics, *flippable later* | **seam — capability, not flippable** |
| deduction | typecheck (shipped) | typecheck (measured 7/0/0) | **build it at the seam** |
| what the move would buy | exact types | nothing | **nothing; it would cost the op vocabulary** |

**[A]** That the three answers differ, and differ for three unrelated reasons, is
the strongest available evidence that per-discipline scoping was the right call.
Perm is the case where the *shared* framing was actively misleading: the plan
carried a promise about perm ("perm inference becomes feasible") that was written
from equiv's premises, survived one correction, and turns out on measurement to
have been pointing away from the cheap fix the whole time.

---

## 10. Reproducing

```
dotnet build Blade.fsproj -c Release
dotnet bin/Release/net7.0/Blade.dll test perm-layer
```

The block prints five probes, the acceptance census in both configurations, the
rejection census with the seam's named offenders marked `*`, the candidate-N
search per function, the three inference configurations with their gate results,
the kit-polarity delta, and the message-parity sweep. It asserts:

0. three shadow-rewrite self-tests, three pin-STRIPPER self-tests and three
   pin-WRITER self-tests (including that a missing declaration is reported
   rather than silently skipped);
1. no perm-rejected file reaches typecheck with any certificate;
2. no typed CONFIRM on a function the seam named as a perm offender;
3. the shadow calibration;
4. non-vacuity of the corpus sweep;
5. **every inference proposal is accepted by the shipped seam checker when
   pinned back**, plus a negative control proving the gate can fail;
6. non-vacuity of the inference sweep.

Everything else is a `[SKIP]` census line, so a corpus change moves the numbers
without turning the block red for the wrong reason.

**Residue.** The block registers a constraint handler named
`__perm_layer_census_shadow` in the process-wide `Blade.Constraints` registry,
which has no unregister. Safe — no test pins the registered-vocabulary list, and
the spelling is unwriteable by accident. (Same residue as `Test_RepRejectCensus`
and `Test_GalLayerCensus`.)

**The instrument is not a discipline.** `tests/Test_PermLayerCensus.fs` §1 is
experimental and must stay in the test assembly. If a decision is taken on the
strength of these numbers, promote it deliberately with its own gate, or delete
it.
