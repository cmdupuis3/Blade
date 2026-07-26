# Plan: Implicit Formers + Signature Deduction ("the deduction triad")

> **Status:** design / plan. Speculative tier â€” not canonical spec. Supersedes and
> extends the prior "Optional object_for/method_for annotations" plan by adding the
> two deduction axes (symmetry, rank) and the confirm-and-pin safety model.
> Canonicality order still applies: Coq proofs > formalism > compiler > this note.
> **Date:** 2026-07-24. **Revised:** 2026-07-25 â€” every code claim verified against
> the tree (file:line cites throughout), pipeline placement decided (single late
> pass, post-monomorphization, IDE-wired), the Â§3.2 mechanism specified precisely,
> and stage 0 (known-bug corrections) executed.

## 1. Goal

Reach a surface where a symmetric-tensor computation reads as ordinary math â€”

```blade
function covariance(a, b) = mean((a - mean(a)) * (b - mean(b)))
let cov = covariance(B, B)          // B : Array<Float like Idx<X>, Idx<Y>, Idx<T>>
// cov : Array<Float like SymIdx<2, compound(Idx<X>, Idx<Y>)>>   (triangular storage)
```

with **no** `method_for`/`object_for`, **no** `for`/`<@>`, **no** `comm` clause,
and **no** rank annotation â€” yet where every one of those erased facts is
*recovered, displayed, and confirmable*, so nothing is magic and nothing that
changes storage or correctness ever ships silently. (The printed `SymIdx`
spelling above is illustrative; the printer format is `SymIdx<rank, dims>`,
src/IR.fs:6011-6016.)

The honest target is **not** "zero annotations." It is:

> **Annotations only where a wrong guess would be silently incorrect â€” and even
> those are proposed by the compiler and confirmed by the user, never demanded
> blank.**

## 2. Background: what is already true

### 2.1 Formers are erasable additively (prior plan)

`method_for`/`object_for` and `for â€¦ in â€¦` are strongly enough typed to become
optional *clarifying annotations* rather than load-bearing syntax.

**Current-code facts (verified):**

- **One seam, three arms.** The `OpApply` arm of `inferBinOp`
  (src/TypeCheck.fs:4171-4183) infers both sides and hands to `inferApply`
  (TypeCheck.fs:5058), which today requires one of THREE left shapes:
  `TExprMethodFor`, `TExprObjectFor`, or `TExprCompose(OpComposeObj, â€¦)` (a
  composed object-loop chain `(o1 >>@ o2) <@> A`, TypeCheck.fs:5356) â€” and
  errors otherwise (catch-all at TypeCheck.fs:5384-5397:
  `ChainOpNeedsMethodFor` / `ChainOpBadKernel`, messages in
  src/TypeEnv.fs:323-325). The right side already gets one leniency:
  `etaExpandFunctionKernel` (TypeCheck.fs:4995-5040) eta-expands a bare named
  function into a lambda. A surface-level normalization at this seam is
  invisible to Lowering, CodeGen, the interpreter, and both differential gates â€”
  and must preserve the compose arm.
- **Purely additive.** Every legal program today already has a former on the
  left, so it takes the unchanged path. The sugar only converts what are
  currently hard errors.
- **`for` is a role-agnostic marker**, not a `method_for` synonym: `ForSource`
  has both `ForArrays` and `ForKernel` arms (src/Ast.fs:423-425), and
  `inferForExpr` (TypeCheck.fs:7860-8037) lowers `ForArrays(_, None)` to
  exactly `TExprMethodFor`. `in` is parsed as part of the arrays tuple
  (src/Parser.fs:2171-2189), so it attaches to the *arrays* operand regardless
  of which side of `<@>` the `for` appears on.
- **Deliberate trap kept:** `(A, B) <@> k` is the rank-2 **outer product**, not
  a zip (corpus: functions/003 documents `M[i][j] = A[i]*B[j]`); `in` is how
  co-iteration is spelled (loops/014: `for (A, B) in range<Idx<3>> <@> â€¦` gives
  the elementwise `[4,10,18]`). This is inherent to structure-first semantics
  and is the *correct* default for the symmetric-tensor domain â€” but see Â§6.2:
  removing the former removes today's only speed bump in front of this trap.

**Design intents validated on the corpus (NOT yet implemented â€” this is Â§7
stage 1):**

- **Right-operand-first classification.** A lambda / section / `reynolds` /
  `zero` on the right is decisive, so `f(x) <@> lambda(...)` can resolve even
  while `f(x)` is still `IRTInfer`; undecidable pairs get a steering
  diagnostic, never a guess. No such logic exists in `inferApply` today â€” the
  dispatch is a hard structural match on the left kind. The claim that this
  rewrite is safe and unambiguous was validated against the corpus during the
  prior exploration; the code change has not shipped.

### 2.2 The formal license

`object_for(f) <@> (A, B) â‰¡ method_for(A, B) <@> f` for rank-0 kernels is
proved: `rank0_convergence` (proofs/BladeCompute.v:189, "Theorem 12.2" in the
Coq comment's legacy numbering) with `compose_apply_duality`
(BladeCompute.v:225-228); in the current v11 formalism this is Â§10.3
(docs/formalism.md:868-869). That equivalence is exactly why erasing the former
is safe for the common case â€” the two constructors denote the same computation
there.

### 2.3 What primitives carry today â€” and what they don't

The op tables in code are **boolean commutative/not**, and the two copies agree
(src/Lowering.fs:905-917 `lowerTypedSection`; src/TypeCheck.fs:5066-5082):

- commutative: `+  *  ==  !=  &&  ||`
- not: `-  /  %  ^  <  <=  >  >=`

There is **no** per-op antisymmetric/asymmetric classification anywhere: the
3-way `SymmetryClass` vocabulary (`SymNone | SymSymmetric | SymAntisymmetric |
SymHermitian`, src/IR.fs) attaches to *index types* (storage descriptors) and
to the explicit `reynolds(kernel, Antisymmetric)` former
(src/Parser.fs:1867-1876) â€” never to a primitive operator. Writing `-` in a
kernel body marks nothing today; it is simply `isComm = false`, same bucket as
`/` and `<`.

A kernel that *is* a bare primitive section already deduces `comm` from that
boolean table â€” but only via the literal-section arm of `inferApply`
(`CommGroups = [[0;1]]`, TypeCheck.fs:5066-5081), reachable only when the
kernel expression is literally `(+)`-shaped. Every compound body â€” lambda or
named function â€” takes the other arm, where `CommGroups` comes *solely* from
the `where comm(...)` clause (`extractCommGroups`, TypeCheck.fs:1136-1142,
which maps names to parameter indices and never inspects the body).

Two consequences the plan must own:

1. **The 3-way parity table (antisym `-`) and the per-operand sign-linearity
   table (Â§3.2) are NEW artifacts of stage 3** â€” they extend, not read, the
   existing boolean tables.
2. **`where comm(...)` is trusted, never checked.** The only validation is an
   index-bounds check (src/IR.fs:6754-6758). A false `comm` on a
   non-commutative kernel silently emits compact storage with corrupt mirror
   reads â€” this hazard exists *today*, for hand-written annotations, not just
   for future deductions. (Stage 0 removed the two corpus tests that had
   checked in exactly this corruption; see Â§7.) The deduction pass therefore
   doubles as the missing validator (Â§4).

**Prior art already on the roadmap:** docs/future.md Â§4b item 4 ("Reynolds
self-licensing") records the special case â€” `reynolds`-wrapped kernels are
commutative *by construction* (K = Î£ gâˆ˜Ïƒ), so the license is derivable. The
general deduction of Â§3.2 subsumes it (`reynolds(k, â€¦)` â‡’ Inv definitionally),
and stage 3 should close that item.

## 3. The deduction triad â€” one mechanism, three codomains

The whole kernel signature â€” argument ranks, symmetry classes, arity behavior â€”
is deducible from the primitive operations in the body, by the *same*
propagate-and-join mechanism, differing only in the lattice:

| Axis | Local rule (per primitive) | Compose by | Lattice |
|------|----------------------------|------------|---------|
| **Rank** | each op imposes a *minimum* rank on its operands (`reduce`/`extents` force â‰¥ 1; `+âˆ’*/` impose only rank-0 compatibility) | take the max of per-position lower bounds | (â„•, max) |
| **Symmetry** | per-op swap class (comm / antisym / asym) + per-operand sign parity (odd / even / âŠ¥) | per-op transfer rules composed bottom-up (Â§3.2) | {Inv, Neg, âŠ¥} with local mirror resolution |
| **Arity** | pack length from the call site | monomorphization worklist (formal model: fold `<*>`, BladeTrinityAsym) | list length |

All three are monotone joins over their lattice â€” decidable, terminating,
abstract-interpretation-shaped. They coexist because they read the same bodies
and never interfere. All three are **interprocedural via kernel summaries**
(Â§3.4): `mean` is user code, not a builtin (no `KwMean` exists; the corpus
defines it identically in arity/021/022/028 as
`reduce(row, (+)) / extents(row)`), so "`mean(a)` forces a â‰¥ 1" is a fact about
`mean`'s *summary*, propagated to its callers â€” the analogue of a cached
derivative in AD.

### 3.1 Rank: minimum = cell rank (body-only, non-negotiable)

`reduce(a, (+)) * b` infers `a : T^1`, `b : U^0`: `reduce` forces `a â‰¥ 1`, the
elementwise `*` forces `b` to rank 0. Inferred rank per variable = `max` over
every position it occupies of that position's required rank.

**Minimum is correct, not merely convenient.** The minimum rank a body forces
*is* the cell (slice) rank; rank polymorphism supplies the frame on top at the
call site (`S = rank(arg) âˆ’ cell_rank` â€” the "rank gap" of
docs/features.md:121; the term exists only in docs, the mechanism in code is
`buildApplyInfo`, TypeCheck.fs:5399, computing `kernelInputRanks` at 5577-5605
and retagging the innermost cell dims `TDimension` at 5628-5638, with frames
materialized by `computeSDimsPerArray` / `buildRawLoopLevels`,
src/IR.fs:1248,1261). Inferring the cell and letting the call add the frame is
the frame/cell model, restated.

**Cell-rank inference is body-only. This is load-bearing.** Letting call-site
argument ranks inform the cell-rank solve would collapse the rank gap: the
kernel would re-infer a different cell rank per call, absorb frame dimensions
into the cell, and stop lifting uniformly over arrays of arbitrary frame rank â€”
i.e. it would trade away rank polymorphism itself. Sharper, because the
tempting mechanism *already exists*: generic HM monomorphization
(`monomorphizeHMFunctions`, IR.fs:3717-3731) already specializes unannotated
functions per call-site type pattern. Â§3.1 is a deliberate opt-out of that
path for `<@>` kernels' cell ranks. The "too-eager minimum" case (a rank-1
passthrough whose body only touches scalars, inferred as rank-0) is handled at
the *signature* by confirm-and-pin (Â§4), never by peeking at the arguments.

**What exists today, what is new.** Rank annotations are already optional
(omitted param/return types become fresh HM unification variables,
TypeCheck.fs:8638-8642) â€” but an explicit `T^k` is an *exact-equality* pin,
enforced through the out-of-band `Subst.arityConstraints` side table
(src/Unify.fs:193-212, 406-432). The proposed lattice is lower-bound/max-join â€”
structurally different â€” so stage 2 adds a new â‰¥-constraint kind beside the
exact one, plus the solve and the display of the deduced cell rank. The base
table also already exists in scattered form and needs consolidating (Â§7 stage
2): `requireArrayArg` (TypeCheck.fs:1455-1484) enforces "array, i.e. â‰¥ 1"
(binding fresh rank-1 when unconstrained) for
intersect/union/unique/contains/sort/gram/mask/extents/group_by; native
`reduce` is rank-1-only (hard error at TypeCheck.fs:3187, with a separate
`rankKDesugar` path for concrete rank â‰¥ 2, TypeCheck.fs:2966-3029); `transpose`
is effectively â‰¥ 2; indexing consumes dims one per index (dimensional
currying, TypeCheck.fs:1764-1806).

### 3.2 Symmetry: adjacent-pair parity, per materialized instance

Deduce the swap-parity of each *sequential pair* of argument positions from the
composed primitive parities of the body.

**Correctness basis (the theorem this rests on):** the symmetric group Sâ‚™ is
generated by the adjacent transpositions (i, i+1). Therefore a kernel invariant
under every adjacent swap is invariant under the whole permutation group â€”
checking the nâˆ’1 sequential pairs certifies full symmetry with no enumeration
of the n! permutations. Deduction pairs and call-site identity groups are
*both* adjacent-based (`partitionIntoIdentityGroups` groups consecutive equal
identities, src/IR.fs:1464-1484; Hâˆ©Stab in code is literally
`inSameCommGroup && sameArrayIdentity`, IR.fs:1581-1585), so nothing is lost
between kernel parity and grouping. The call site still forms identity groups
from neighboring identical arguments (`(A,A,B)` â†’ a group of 2 + a singleton â€”
confirmed live by corpus symmetry/006) and licenses symmetry only where
kernel-parity **and** array-identity agree (H âˆ© Stab). Parity is deduced per
kernel *instance* â€” instantiation-dependent (arity, rank) but always
**identity-independent**: the deduction never asks *which* arrays; the call
supplies identity, exactly as before.

#### 3.2.1 The judgment (the "multiply parities" slogan made precise)

For the adjacent transposition Ïƒ of one argument pair, walk the typed body
bottom-up. Each subtree gets a value in the 3-point lattice

- **Inv** â€” e[Ïƒ] â‰¡ e (does not mention the pair, or is built symmetrically),
- **Neg** â€” e[Ïƒ] â‰¡ âˆ’e,
- **âŠ¥** â€” unknown (always safe: no claim, dense storage),

driven by TWO per-primitive tables:

1. **Swap class** (commutative / antisymmetric / asymmetric). Applied *locally*
   at a binary node whose two children are a **mirror pair** â€” `l[Ïƒ] â‰¡ r`,
   detected by structural comparison of the Ïƒ-substituted subtree (e.g.
   `a âˆ’ mean(a)` â†¦ `b âˆ’ mean(b)`). A comm op on a mirror pair â†’ Inv; an
   antisym op (`-`) â†’ Neg; else âŠ¥. Mirror is a *pairwise, sibling-scoped*
   fact resolved into Inv/Neg/âŠ¥ on the spot â€” it does not propagate upward as
   a value of its own.
2. **Sign parity per operand** (odd / even / âŠ¥). Propagates a child's Neg
   upward: `*` and `/` are odd in each operand, and **signs compose
   multiplicatively across operands** â€” NegÂ·Neg = Inv (the regression case:
   `(aâˆ’b)*(aâˆ’b)` is Inv, not Neg; a naive any-Neg-wins fold is unsound here).
   `+` and `-` (in their non-mirror role) require both children to carry the
   *same* parity (Inv+Inv â†’ Inv, Neg+Neg â†’ Neg, mixed â†’ âŠ¥). Literal integer
   powers: even exponent kills Neg, odd preserves it, exponent 0 â†’ Inv;
   non-integer or non-literal exponents â†’ âŠ¥ (`^` lowers to a generic C++
   `pow(l, r)`, src/EmitCpp.fs:897,1059,1366). Comparisons and boolean ops
   absorb Neg (sign parity is a numeric-only notion). Linear reductions â€”
   `reduce(_, (+))`, and `mean` via its summary â€” pass parity through.
   **Negative rules are explicit, not by analogy:** `reduce(_, (*))` and any
   min/max-style or user combinator do NOT pass parity (the sign under swap is
   (âˆ’1)^extent, unknowable unless the extent is static); `%` is âŠ¥.

Rules of the game: all structural comparisons are over **binder identity
(VarId)**, never surface names (the corpus's recursive kernels rebind
`head`/`tail` in every match arm); mirror detection is purely syntactic in v1 â€”
**no AC-reassociation** â€” so `(a*d)*b` vs `b` (a normalization constant
interposed between mirror factors) is an accepted incompleteness that lands on
âŠ¥/dense, never on a wrong claim; and the tables are **closed-world**: any
primitive without an entry is âŠ¥ by default, so new builtins are conservative
until classified.

Without table 2, no *compound* antisymmetric kernel â€” e.g. `(aâˆ’b)*(a+b)` â€” is
deducible; table 2 is the chain rule of the autodiff analogy, and the kernel
summaries of Â§3.4 are its cached derivatives.

This is the compositional generalization of BladeCompute's
`deduced_commutativity` (proofs/BladeCompute.v:286-322 â€” the Coq comment's
"9.19/9.20" tags are legacy v10 numbering; the current v11 analogue sits
unnumbered under formalism.md Â§11.2 "Raising"), which today proves only the
specific `S(i,j)Â·A(i)Â·A(j)` shape â€” the Coq comment says so verbatim.

#### 3.2.2 Arity-polymorphic kernels: per-instance, not âˆ€-arity

"Check pairs up to max arity N" cannot terminate for `Poly<T^k>` kernels â€”
arity is unbounded. It also doesn't have to: deduction runs **after
monomorphization** (Â§3.4), so it checks the nâˆ’1 adjacent pairs of each
*materialized* arity on the specialized bodies, walking the finite
specialization DAG (packprod@2 consumes packprod@1's summary). The
Sâ‚™-generation lemma stays load-bearing per instance â€” nâˆ’1 pairs instead of n!
permutations â€” and fixed-arity kernels are simply single-instance cases.

The âˆ€-arity claim (one pin covering *every* arity) reduces, for head::tail
recursion `f(x :: rest) = g(x) âŠ› f(rest)`, to (a) the **exchange law**
`x âŠ› (y âŠ› R) â‰¡ y âŠ› (x âŠ› R)` â€” which follows from assoc+comm of `âŠ›` â€” plus (b)
the **final step-vs-base pair** (the two checks are independently necessary:
the last pair's second position is a bare base-case leaf, not a `y âŠ› R` shape).
That induction moves to the Coq general lemma (Â§8) and to an optional
strict-mode check for library kernels with no in-tree call sites (Â§6.1); it is
not v1 machinery. Every recursive Poly kernel in the corpus (arity/020-028)
matches the assumed base = `g(head)` / step = `g(head) âŠ› tail` template.

**Antisymmetry falls out with correct signs â€” at fixed arity.** The same
pairwise pass with table 2 yields the alternating/sign structure for `-`-built
kernels per instance. But arity-*uniform* antisymmetry via the recursion is
essentially vacuous: an antisymmetric combiner cannot satisfy the signed
exchange law (`g(a) âˆ’ (g(b) âˆ’ g(c))` is not alternating â€” only the final
step-vs-base pair is antisymmetric, by the combiner's own definition). Scope
antisym deduction to fixed arity, which per-instance checking gives for free.

### 3.3 Arity

Unchanged from today: pack length drives loop depth, output rank, and per-group
symmetry via the identity-group model. Two mechanisms coexist and should not be
conflated: the `<*>` operator (`OpArrayProd`, src/Ast.fs:77) concatenates
method_for ARRAY lists (`method_for(A) <*> method_for(B) â‰¡ method_for(A,B)`,
formalism.md:840-846) and is the *formal model* ("runtime-arity loops:
`fold(<*>, map(method_for, arrays))`", BladeTrinityAsym); the kernel-parameter
pack (`Poly<T^k>`) axis is *implemented* as compile-time monomorphization â€”
`computePolyArity` (src/IR.fs:4389) reads pack length from the call site,
`specializeFunction` (IR.fs:4465; comm-group expansion at 4751-4783) clones per
arity, and `monomorphizeModule` (IR.fs:5809-5860) drives the worklist to a
fixpoint. Arity polymorphism is already the forced closure of {loop
reification, dimensional currying} under `<*>` (BladeTrinityAsym).

### 3.4 One pass, late: placement and summaries

**Decision (2026-07-25): comm deduction runs once, post-monomorphization, and
is wired into the IDE's checking path.** Rank stays early and body-only. The
asymmetry is intentional: *rank defines the kernel's type* and must be stable
across calls (Â§3.1); *comm feeds per-call storage* and may be checked per
instance. The late pass consumes resolved ranks/arities to enumerate instances;
it never feeds them back into the cell-rank solve.

- **Where:** a new `Deduce.fs` after IR.fs in the .fsproj, invoked from
  Lowering.fs's single post-`monomorphizeModule` chokepoint, walking
  specialized bodies (IRExpr) â€” the level where every arity and rank is a
  concrete int and where the boolean isComm table and the Hâˆ©Stab consumers
  already live. (An earlier idea â€” running inside `typeCheck`'s tail â€” is
  superseded; `typeCheck` has five call sites, the lowering chokepoint has
  one.)
- **Summaries:** per instance, computed in specialization-DAG order; monomorphic
  kernels are single instances computed once and reused by all callers
  (`mean`'s summary serves every `comoment@k`). Finite by construction â€” no
  unbounded fixpoint on the comm axis. (The rank axis, being body-only and
  pre-monomorphization, does need a self-recursion fixpoint with a divergence
  guard â€” Â§6.5.)
- **Provenance:** per-instance results aggregate back to source decls via
  `specializeFunction`'s per-original-index (newStart, span) mapping
  (IR.fs:4751-4783); diagnostics carry source spans of the decl, not the
  specialization. Aggregation must be deterministic (Â§6.6).
- **IDE:** `blade ide check` (src/Ide.fs:750-805; wired at src/Cli.fs:1058)
  today stops after typecheck. It gains the lowering prefix â€” lower â†’
  monomorphize â†’ deduce, **no CodeGen**. Measured cost on this machine
  (Release build, one-shot process): `ide check` 0.51 s / `emit` 0.73 s on a
  23-line Poly corpus file, 1.02 s / 1.34 s on the largest (23 KB) corpus file â€”
  and the 0.22â€“0.32 s delta *includes* full C++ codegen and its JIT, which the
  deduction prefix skips. The marginal cost of monomorphize+deduce at IDE scale
  is milliseconds against a ~0.5â€“1 s process-spawn baseline: imperceptible. If
  that baseline ever matters, the levers are orthogonal (ReadyToRun/AOT, a
  persistent daemon) â€” plus an in-design fallback: the pass needs only
  call-site arities/type-patterns, not monomorphized code, so a summary-level
  instantiation driver could run even earlier if ever needed.

## 4. Confirm-and-pin: the safety model

Erasing a *former* that guesses wrong is a type error (safe). Acting on a wrong
*commutativity* or *rank* is silent incorrectness (triangular storage on a
non-symmetric output, or a mis-split S/T) â€” a wrong answer, not a slow one. The
resolution:

> **Unconfirmed deduction â‡’ dense / literal (correct, unoptimized). Confirming â‡’
> the user opts into the compact storage or the inferred rank.**

- Until pinned, an inferred symmetry class compiles to **plain dense storage** â€”
  always correct. This is not a new lowering path: `classifyOutputStorage`
  (src/IR.fs:2714-2745) already bottoms out at `AllocDense` whenever no comm
  group or symmetric input marks the output. The deduction surfaces as a
  suggestion ("this kernel deduces commutative in (a,b) â†’ `SymIdx<2,Â·>`; pin
  it?"), and pinning is the user taking responsibility for the speedup.
- Wrong-guess state is therefore **correct-and-dense**, never
  **compact-and-corrupt**.

**The threat model, stated honestly.** With Â§8's soundness lemma in place, the
deduction never *affirms* wrongly â€” Inv/Neg are proved, âŠ¥ is the only honest
"don't know". The reasons to pin are then: (i) interim safety until the Coq
lemma lands; (ii) **edit fragility / interface stability** â€” a body edit that
silently loses symmetry would silently fatten (or, if storage were automatic,
re-layout) a published type; a pin is a *checked contract* that errors when the
pinned class is no longer deducible, exactly like a type annotation on an
inferred type; (iii) intent documentation.

**Pinning introduces no new syntax for comm or rank** â€” the pin artifact IS
today's `where comm(...)` / `T^k` annotation, which is also what makes Â§6.1's
"the annotation in source is the durable artifact" story work.

**The two-regime policy (and the retroactive payoff).** Today `where comm(...)`
is *trusted*, never checked (Â§2.3) â€” the validator is the missing half of the
existing feature, not just of the new one. Once deduction lands, per materialized
instance:

- declared comm + deduced **Neg** â†’ **hard error** (v1 errors only on this
  affirmative case â€” there is deliberately no disprover, so the corpus's
  deliberate comm-licenses on âŠ¥ bodies keep compiling);
- declared comm + **âŠ¥** â†’ trusted escape hatch, status quo (optionally an info
  note) â€” this regime also legitimizes the sanctioned Reynolds idiom, where
  `comm` on the wrapped kernel is an iteration license (symmetry/019) until
  future.md Â§4b.4's self-licensing subsumes it;
- declared comm + deduced **Inv** â†’ a *checked* pin, validated at every
  materialized use in every compilation. A pin can no longer be silently wrong
  in a program that compiles (modulo âŠ¥ instances).

**Antisymmetry: storage is ready, the pin spelling is not.** Antisym storage is
fully implemented end-to-end â€” `AntisymmetricBehavior` (src/IR.fs:2647-2656:
canonical strict sort, implicit-zero diagonal, negate-on-swap reads),
`AllocAntisymmetric` routing (IR.fs:2707-2745), rank-2 and rankâ‰¥3 codegen,
interpreter mirror (src/Interp/ArrayOps.fs:290-337), `AntisymIdx` corpus
coverage â€” but there was no `where antisymm(...)`: `CnAntisymm`
(src/Ast.fs:233-241) was dead, unparsed code, and antisym output was reachable
only via already-antisym inputs or `reynolds(kernel, Antisymmetric)`, leaving a
deduced-Neg kernel with nothing to pin to. **Landed 2026-07-26** (Â§7 stage 3):
the clause parses, groups like `comm`, flips the group's stored class to the
strict simplex, and is validated against the deduction at both seams. The
formal side was already in place: kernel anti-invariance â‡’ output antisymmetry
is the sign-tracked variant of the lowering law (formalism.md:949-950).

**IDE affordance â€” display exists, write-back does not.** There is no LSP; the
tooling surface is the one-shot `blade ide check --json` (bindings/providers
JSON consumed by an out-of-repo VS Code extension) plus the REPL's shared
abstract-type renderer (src/Cli.fs:266-314). The ghost annotation rides that
existing display surface (a new JSON field: suggested annotation + insertion
span). The one-click pin is new, strictly extension-side work (apply the edit
at the span); nothing in this repo writes source. Non-IDE users pin by typing
the annotation â€” same artifact, same semantics.

- The rank axis pins the same way: inferred cell rank is shown; if the minimum
  is wrong for intent, the user pins the higher rank with an ordinary `T^k`
  annotation. Same escape hatch, same gesture.

## 5. Worked example

```blade
// Source, fully implicit:
function covariance(a, b) = mean((a - mean(a)) * (b - mean(b)))
let cov = covariance(B, B)

// Inferred signature (shown, pinnable):
//   covariance : (a: T^1, b: T^1) where comm(a, b) -> T^0
//     rank    : mean/reduce force a â‰¥ 1, likewise b â†’ T^1; outer mean â†’ T^0
//     parity  : Ïƒ = swap(a, b) maps (a âˆ’ mean(a)) to (b âˆ’ mean(b)) â€” a mirror
//               pair; * is commutative â†’ Inv; the outer mean is linear and
//               passes Inv through â†’ commutative in (a, b)
// Call cov = covariance(B, B):
//     B is rank-3, cell rank 1  â†’ 2 S-dims per arg (rank gap â†’ auto-lift)
//     args identical (B, B)     â†’ one identity group of size 2
//     comm âˆ§ identity           â†’ SymIdx<2, compound(Idx<X>, Idx<Y>)>
// cov : Array<Float like SymIdx<2, compound(Idx<X>, Idx<Y>)>>   (triangular)
```

No former, no `<@>`, no `for`, no `comm`, no rank â€” all recovered and
displayed. The corpus already holds this program in today's explicit syntax:
arity/027 (`packprod` + `where comm(a)` + `object_for â€¦ <@>`) and arity/028
(`comoment` â€” literally `mean(comoment_prod(a))`) are the "before" pictures of
this section, and arity/022 vs 028 is the dense-vs-pinned storage pair (four
cells with a duplicated `1.333` vs the 3-cell triangle).

## 6. Open questions / risks

1. **Separate compilation and type providers â€” no human to confirm.**
   Confirm-and-pin assumes an interactive confirmer. Under batch compilation, a
   provider-minted type, or an imported module, there is nobody to click.
   Options: (a) unconfirmed = dense is the *terminal* state in non-interactive
   builds (safe, leaves speedups on the table until someone pins them in
   source); (b) a pinned annotation in source is the durable artifact and CI
   fails on *unpinned* deductions that would change storage. Leaning (a) as
   default with (b) as an opt-in strict mode â€” strengthened by Â§4's checked-pin
   semantics, and the natural home for the library-author exchange-law check
   (Â§3.2.2) for Poly kernels with no in-tree call sites (which otherwise get no
   instances, hence no deduction and no suggestions â€” safe, but silent).
   **DECIDED AND LANDED (2026-07-26): (a) default, (b) opt-in as
   `--strict-pins`.** The flag is accepted by `check` / `compile` / `emit` /
   `run` (a build MODE, stripped from argv ahead of the verb patterns, so it
   composes with every existing arm shape); it reads the `PinSuggestions`
   side-channel `typeCheck` fills and re-emits every outstanding suggestion as
   an ERROR-severity `BL4010` at the kernel span, exit 1. Two gate sites, one
   per surface: `checkFile` (which drops the plain-string warning twins, the
   same dedup `blade ide check` does) and `compileFile`, which fails before
   codegen and so covers compile/emit/run at once. Deduplicated, in deduction
   order (Â§6.6). Without the flag nothing changes â€” suggestions stay warnings,
   storage stays dense-until-pinned. `blade ide check` is deliberately NOT
   wired: an editor wants a ghost annotation, not a failed build. Still open
   here: the library-author exchange-law check for Poly kernels with no in-tree
   call sites (no instances â‡’ no deduction â‡’ nothing for strict mode to fail
   on â€” safe, but silent).
2. **Elementwise-vs-outer default never fully disappears â€” and stage 1 sharpens
   it.** `covariance(B,B)` defaulting to outer is correct for this domain, but
   every elementwise use still needs `zip`/`in`. Today the explicit
   `method_for`/`object_for` incantation is itself a speed bump that makes a
   user pause; the implicit-former rewrite removes exactly that guard rail, and
   no outer-vs-zip diagnostic exists in Diagnostics.fs. The loud diagnostic for
   the numpy-shaped mistake is therefore a **stage-1 co-requisite**, not a
   deferred nicety.
3. **Identity is by-name.** `let C = B; covariance(B, C)` deduces *no* symmetry
   (distinct names â‡’ Stab = {id}), even though `C` is `B` â€”
   `ArrayIdentity`/`sameIdentity` compare variable names structurally
   (src/Types.fs:61-73), and the formalism is explicit that identity is
   required, full stop (formalism.md:927-931). As the surface gets cleaner this
   invisible rule gets sharper â€” it needs a first-class diagnostic, precisely
   because there is no longer a `method_for` on the page to point at. (corpus
   functions/003's own comment already documents the distinct-arrays case.)
   Scoped future path: `AIDDerived` (Types.fs:61-73) is dead identity
   infrastructure â€” never constructed, not even self-equal in `sameIdentity` â€”
   that could carry derived identities like `let C = transpose(B)` without
   solving general aliasing.
4. **Too-eager minimum rank** (Â§3.1): handled by confirm-and-pin, but the
   diagnostic must make the inferred S/T split legible.
5. **Rank-fixpoint divergence.** Recursive kernels can impose unbounded rank
   demands (`f(a) = f(reduce(a, (+)))` gives r = r+1); the body-only rank solve
   needs an occurs-check-style divergence diagnostic. (The comm axis has no
   such risk â€” its DAG is the finite specialization set.)
6. **Determinism.** Suggestion/validation diagnostics must be stable across
   runs for Â§6.1(b)'s CI mode; per-instance aggregation must not leak
   specialization order into diagnostic text.
7. **Float non-associativity.** The mirror-pair and sign cases are bit-exact
   under IEEE (negation, `+`/`*` commutation, sign propagation through `*`/`/`
   are exact, and the v1 judgment never reassociates the as-parsed tree). The
   *pack-recursion* exchange law, by contrast, reorders an associative fold â€”
   true over the reals, ulp-level over floats â€” the same trust level as today's
   hand-written `comm` on a pack. Note the differential-oracle tolerance
   implication; the Coq statements (Â§8) are over the abstract semiring.

## 7. Implementation staging

0. **Correct known bugs â€” DONE (2026-07-25).** The review found the corpus had
   checked in the exact corruption Â§4 exists to prevent: index-types/050
   declared `where comm(x, y)` on `x âˆ’ y` and asserted the silently-wrong
   inclusive-triangle compaction (its own header even said strict `i < j` was
   intended); index-types/057 claimed to test "AntisymIdx in a function
   signature" while containing no `AntisymIdx` and the same lying lambda.
   Fixes: 050 rewritten onto the sanctioned producer â€”
   `reynolds(g, Antisymmetric)` with the comm iteration license on the wrapped
   kernel (cf. symmetry/019) â€” with hand-computed strict-triangle EXPECTs;
   057 retired (superseded verbatim by 058_antisymidx_decl_param); 051's
   header corrected (it is the SymIdx contrast baseline, not an "antisymmetric
   output" producer). Audit outcome: every other declared-comm-on-noncomm site
   in the corpus is reynolds-wrapped (the license idiom, values are the true
   signed antisymmetrization) â€” no further live corruption. Under Â§4's v1
   policy only affirmative-Neg bodies error, so no other test's behavior
   changes when the validator lands.
1. **Optional formers â€” DONE (2026-07-25)** (prior plan, purely additive).
   Landed as the right-operand-first normalization in `inferBinOp`'s `OpApply`
   arm (src/TypeCheck.fs): a syntactic former on the left takes the unchanged
   path (including the composed-loop arm); otherwise a decisive kernel on the
   right (lambda / section / reynolds / zero / eta-expanded named function)
   synthesizes `method_for` around the left operand (tuple â†’ arrays; single
   expr â†’ one-array map; zip â†’ co-iteration), and a kernel-shaped left with a
   non-kernel right synthesizes `object_for` â€” both by re-driving the same
   `inferMethodFor`/`inferObjectFor` the keywords use, so Lowering, CodeGen,
   the interpreter, and both differential gates see identical typed nodes.
   Undecidable pairs get the steering diagnostic (`ChainOpUndecidable`,
   BL3007), never a guess. The Â§6.2 co-requisite shipped with it: implicit
   `(A, B) <@> kernel` over distinct co-iterable operands with a non-comm
   kernel emits the outer-product steering note (`warnImplicitOuterProduct`);
   suppressed for same-array, comm-annotated, or explicit spellings. Corpus:
   loops/101-107 (tuple/single/comm-SymIdx/object_for/named-kernel/zip/reject);
   full suite 956 corpus + 1272 total, 0 failed; interp differential clean.
   (`for`/`in` grammar needed no work â€” already role-agnostic, Â§2.1.)
   **Left-side bare named kernels closed (2026-07-26).** The residue
   classification only recognized RESOLVED kernels
   (`TExprLambda`/`Section`/`Reynolds`/`Zero`), and a top-level `function`
   binds with `TypedValue = None` â€” precisely the case `resolveTypedExpr`
   cannot surface â€” so `covariance <@> (data, data)` fell through to the
   `ChainOpUndecidable` steering error even though `object_for(covariance) <@>
   (data, data)` worked. `inferBinOp`'s `OpApply` arm now classifies a bare
   named function on the LEFT the same two ways `inferObjectFor` classifies
   its own kernel â€” fixed arity (eta-expandable) and Poly pack (eta refused,
   deferred former with `Arity = None`, expanded at this very seam) â€” and
   routes both through `inferObjectFor`, so the implicit spelling IS the
   explicit one: same typed nodes, same stage-3 suggestions with the callee's
   real parameter names. Guarded behind the decisive-RIGHT arms, so a bare
   name meeting a lambda still reads as the arrays operand and two arrays
   still steer (loops/107). Corpus: loops/108 (fixed arity, the functions/026
   values) and loops/109 (Poly, the arity/029 values).
2. **Rank-from-primitives â€” CORE DONE (2026-07-25), scoped.** Landed: the
   `rankLowerBounds` side table on `Subst` (src/Unify.fs) beside the exact
   `arityConstraints` â€” max-join on registration, validated/propagated in
   `unify`'s var arm; rank propagation at the DIRECT-APPLICATION seam
   (src/TypeCheck.fs `dispatchAppOrIndex`, the third strictness carve-out
   after irreps and units): a callee parameter's (possibly itself deduced)
   rank is imposed as a lower bound on still-unresolved argument vars â€”
   concrete arguments keep the historical looseness; and decl-close pinning
   in `checkFunctionDecl`: an unannotated param still unresolved but carrying
   a bound k pins to a fresh rank-k array with a free element type (body-only
   by construction â€” bounds only ever come from the body's own uses).
   `function total(row) = reduce(row,(+)); function twice(a) = total(a) +
   total(a)` now deduces, compiles, and runs â€” previously it typechecked
   silently and emitted ill-typed C++ (corpus functions/023). Discovered and
   fixed along the way: the array<->scalar broadcast kernel inlined its fixed
   operand with no captures, dangling any function-local VarIds (BL6001) and
   recomputing the scalar per element â€” computed scalars now hoist into a
   let and thread in as a proper capture (src/Lowering.fs
   `lowerTypedPartialAppWith`; corpus functions/024; pre-existing bug,
   exposed by rank deduction, verified with an annotated repro).
   Deliberately deferred: the declarative min-rank table (builtins keep
   their existing enforcement sites â€” `requireArrayArg` et al. â€” until ops
   gain lifted implementations); a dedicated divergence guard (with exact
   pins retained, the pathological self-recursive demands surface as plain
   type errors today); rank display polish (deduced ranks materialize in the
   resolved signature and flow to the existing type renderers).

2b. **Expression-position loop materialization â€” DONE (2026-07-25), scoped.**
   Plain (non-Poly) functions whose bodies produce arrays via synthesized
   loops previously reached C++ emission as the "loop object used as value"
   sentinel (a pre-existing limitation; Poly kernels sidestep it because
   monomorphization inlines pack-element ops, which is why the corpus never
   hit it). Landed in `genFuncBody` (src/CodeGen.fs): a bottom-up hoist
   pulls every `IRApp(IRObjectFor â€¦)` â€” and any IRLet chain wrapping one,
   e.g. the stage-2 broadcast's hoisted scalar â€” into the flat let list in
   dependency order, and a new dispatch arm routes each through
   `genBinding`'s existing loop-nest materializer (module-level parity, the
   same pattern as the IRForRange/IRArrayLit arms). **The Â§5 flagship now
   runs fully unannotated in both forms**: `covariance(x, y)` directly
   (corpus functions/025) and `object_for(covariance) <@> (data, data)`
   over rank-2 data (functions/026 â€” dense 2Ã—2, identical values to
   arity/022, compaction awaiting the stage-3/4 comm pin as designed).
   **Return-extent ABI â€” DONE (2026-07-26).** *Returning* a
   loop-materialized array from a plain function was the remaining work
   item, guarded with an emitted `#error`; the guard is now gone and
   functions/027 is a value-pinned passing test (`center(a) = a - mean(a)`
   over `[1,2,3]` â†’ `[-1, 0, 1]`). The diagnosis was narrower than
   "companion extents don't cross the boundary": nothing caller-side needs
   plumbing at all â€” the caller binds the returned `Array<T,R>` and reads
   shape straight off it (`c.extents[0]`), never off a companion
   `c_extents`. The wrapper holds a *pointer* to its extents table, and
   `genObjectForApplication` declared that table as a frame-local
   `size_t[R]`; the data pool (heap, via `allocate<>`) outlived the frame
   but the shape pointer dangled, so the caller read garbage. The fix is
   one line per output shape: heap-allocate the extents table
   (`size_t* R_extents = new size_t[R]`), which is exactly what
   `genApplyCombinator` already did â€” and precisely why *its* array
   returns (`method_for(a) <@> k |> compute` as a function body) had always
   worked. Reads are unchanged (`R_extents[d]` indexes a pointer the same
   way it indexed an array), and lifetime matches the data pool's. Verified
   for the broadcast, two-array elementwise, and outer-product shapes, for
   rank-2 outputs, and for nested calls (`center(center(x))`); the
   interpreter gate agrees. Still LOUD and out of scope: returning an
   *inline form* (`mask`/`sort`/`unique`/â€¦) from a function body, which
   fails on a different missing lift (unsupported-IR-node sentinel in
   return position), not on extents.
3. **Symmetry-from-primitives â€” EARLY TIER DONE (2026-07-25), scoped.**
   Landed as `src/Deduce.fs` (pure analysis over TypedAst, compiled before
   TypeCheck so `typeCheck` invokes it internally â€” the Zonk pattern): the
   {PInv, PNeg, PBottom} judgment with the two per-primitive tables (3-way
   swap class consulted at sibling-scoped MIRROR nodes; per-operand sign
   composition with multiplicative PNegÂ·PNeg = PInv), mirror equality by
   binder identity (VarId, never surface name), and a closed-world PBottom
   default for every unlisted node kind. Wired at the ONE seam every apply
   arm funnels through (`buildApplyInfo`), plus per-function summaries
   (`FuncDeducedPairs`: param names + adjacent-pair parities, recorded at
   `checkFunctionDecl`, consulted by eta-expanded wrappers so
   `object_for(f)` sees f's deduced symmetry). Behavior:
   - **Validation (retroactive hardening of Â§4's trusted gap):** declared
     `where comm` on a body deduced PNeg is a hard error
     (`CommContradictsBody`, BL3007) â€” at the decl for named functions
     (which can never be reynolds kernels), at the apply seam for lambdas.
     Under `reynolds` the clause is an ITERATION LICENSE and validation
     stands down. PBottom stays trusted (the escape hatch). Corpus:
     functions/028 (lambda), functions/030 (named decl); the reynolds
     corpus, including the rewritten index-types/050, is untouched.
   - **Confirm-and-pin suggestion:** kernel deduced PInv in an adjacent
     pair, no comm declared, SAME array in both positions (H âˆ© Stab would
     license) â†’ a warning proposes the exact pin, with the callee's real
     parameter names through the eta wrapper. Output stays dense until the
     user pins â€” observationally inert, as staged. The flagship closes:
     functions/026 (unpinned, dense 4 cells + suggestion) vs functions/029
     (the suggested one-line pin added â†’ SymIdx<2,2> triangle, 3 cells,
     values identical across both backends).
   **Late tier â€” PACK KERNELS DONE (2026-07-25), via the âˆ€-arity exchange
   law rather than per-instance IR checking.** The corpus's pack kernels
   are head::tail AC-folds (or wrappers over them), so the decl-level
   check the plan kept in reserve is both simpler and stronger than
   per-instance enumeration: `Deduce.deducePackFold` recognizes the
   canonical template â€” `match arity(pack) | 1 -> g(head) | _ ->
   g(head) âŠ› f(tail)` with âŠ› associative AND commutative (+ * && ||),
   base â‰¡ step g (mirror equality over the two arms' head binders), no
   g touching tail or the pack â€” which is symmetric at EVERY arity by
   the AC-fold induction. Wrappers (`comoment = mean(comoment_prod(a))`)
   inherit compositionally via `Deduce.packParityOf`: invariance under
   pack permutation composes through every operator, with whole-pack
   calls resolving against the `PackDeducedComm` summary table in decl
   order. Packs only ever claim PInv or PBottom â€” the review proved no
   signed exchange law exists, so pack deduction fuels SUGGESTIONS only
   and can produce no false errors; declared `where comm(pack)` stays
   trusted (027/028 unchanged). The suggestion fires at the
   deferred-former eta seam (no declared comm + pack-PInv + identical
   identities), spanned to the source kernel, alongside a BL4010 entry.
   A third link closes the chain: a FIXED-ARITY wrapper over a
   pack-summarized kernel (`lambda(x, y) -> comoment(x, y)`) specializes
   pack invariance to full pairwise symmetry at that arity â€” the early
   tier's summary lookup falls through to `PackDeducedComm`, replicating
   PInv across the wrapper's pairs and naming the LAMBDA's params (where
   that spelling pins). Corpus: arity/029 (the comm-less twin of 027 â€”
   dense 9 cells + the suggestion naming `where comm(a)` on `packprod`);
   arity/022 now earns its suggestion through all three links (template â†’
   wrapper walk â†’ fixed-arity specialization).
   **INTERPROCEDURAL SIGN-LINEARITY DONE (2026-07-26)** â€” table 2 lifted
   from primitives to whole callees, which is what lets a CALL propagate
   PNeg at all (the old rule could only ever say "PInv when callee and
   args all are", so `mymean(x âˆ’ y)` sat at PBottom however linear
   `mymean` was). `Deduce.deduceSignParities` summarizes each fixed-arity
   function as one {SOdd, SEven, SUnknown} per parameter â€” SOdd meaning
   `f(.., âˆ’x, ..) â‰¡ âˆ’f(..)` â€” bottom-up from the same style of table as
   the pair parities, with the subtle entries being: `extents(âˆ’x) =
   extents(x)`, so an ODD child yields an EVEN extents (this is exactly
   what makes `reduce(row,(+)) / extents(row)` odd overall); `reduce`
   passes the sign only through an UNSEEDED `(+)` (a `(*)` fold scales by
   (âˆ’1)^extent, a seeded fold adds an unnegated accumulator); indexing
   passes the array's sign only when every index is even; `if` needs an
   even condition and matching branches; tuples/structs have no negation
   as a value operation, so only invariance composes; comparisons and
   logicals are even-only. Summaries live in `TypeEnv.FuncSignParities`,
   recorded at `checkFunctionDecl` in DECL ORDER â€” a self- or
   forward-call resolves to None and lands on SUnknown, so there is no
   fixpoint and no summary proves itself. Keyed by the function's BINDER
   ID rather than its name (unlike the sibling tables): a parameter
   shadowing a function's name must not borrow that function's sign law,
   since a wrong sign law is a wrong parity is a wrong pin. `parityOf`'s
   call rule then applies the chain rule: the swap flips argument i
   exactly where its pair-parity is PNeg, and the flip reaches the result
   exactly where the callee is SOdd in position i, so the call is (âˆ’1)^k
   times itself.
   Corpus: functions/031 (`where comm` on `mymean(a âˆ’ b)` now hard-errors
   at the decl â€” antisymmetric THROUGH the helper), functions/032
   (`half(x âˆ’ y) * half(x âˆ’ y)` earns PInv by PNegÂ·PNeg where the mirror
   rule cannot fire, suggestion + dense 9 cells).

   **`where antisymm(...)` pin spelling â€” DONE (2026-07-26).** The signed
   half of confirm-and-pin: `antisymm` is now a where-clause keyword
   (`WhereClause.Antisymmetry`, `TypedLambdaInfo.AntisymGroups`,
   `IRCallable.AntisymGroups`; the dead `CnAntisymm` case is documented as
   the superseded design, like `CnEquiv`). A declared group is the SAME
   axis grouping and the SAME iteration license as a comm group â€” it rides
   `CommGroups` for every grouping consumer â€” and carries exactly one extra
   bit: the licensed simplex is the STRICT one. That bit lands in the two
   places storage is decided, `deduceOutputType` (group symmetry
   SymAntisymmetric â†’ `AntisymIdx<r, n>` â†’ AllocAntisymmetric) and
   `buildLoopNestCodeGen`'s per-level `StrictOffset` (i < j, no diagonal to
   write), so `method_for(A, A) <@> lambda(x, y) where antisymm(x, y) ->
   x - y` reaches the existing antisym storage WITHOUT reynolds: the kernel
   is used as-is (store f(i,j) for i<j; the mirror read negates via the
   index type's TfNegateOnSwap), no permutation sum. Named functions pin
   through their own `FuncAntisymGroups` side-channel at the eta seam (a
   clause the wrapper does not re-attach is dropped silently). Validation
   is symmetric with comm's, at both seams: declared antisymm + deduced
   **PInv** = hard error (`AntisymmContradictsBody`, BL3007); PBottom stays
   trusted; under reynolds the clause degrades to an iteration license and
   both the validator and the storage bit stand down, so the reynolds
   corpus is untouched. Deduced PNeg + no declaration + same array in both
   positions now suggests `where antisymm(x, y)` beside the existing comm
   suggestion (same warning + BL4010 pair). Corpus: symmetry/020 (pinned
   strict triangle, values identical to 019's reynolds twin), 021 (declared
   antisymm on `x * y` rejects), 022 (unpinned twin â€” dense 9 cells + the
   suggestion), 023 (named-function pin through the eta wrapper), 024
   (`comm` and `antisymm` over the same pair rejects â€” one axis group cannot
   be inclusive and strict at once); all pass the interpreter differential.

   Still deferred after both: per-instance checking on specialized IR
   bodies â€” only needed for NON-template pack kernels, which today deduce
   PBottom and stay inert. Antisymmetry has no pack tier by construction â€”
   no signed exchange law exists â€” so `antisymm` is deliberately not
   surfaced onto Poly-pack eta wrappers.

   *(original stage-3 text follows)* **Symmetry-from-primitives** (single late pass, Â§3.4): the two per-primitive
   tables (3-way swap class; per-operand sign parity) + the Â§3.2.1 judgment as
   a bottom-up fold over specialized bodies in `Deduce.fs`, invoked from
   Lowering's post-monomorphize chokepoint; per-instance summaries over the
   specialization DAG; provenance aggregation to source decls; **validation of
   existing `where comm` annotations (declared-comm + deduced-Neg = error)**;
   suggestion diagnostics; `blade ide check` gains the
   lowerâ†’monomorphizeâ†’deduce prefix (no CodeGen â€” measured budget in Â§3.4).
   Architectural shape: the per-primitive-table + recursive-walk pattern of
   Grad.fs's `derivRule`/`adjointOf` (Grad.fs:117-132, 1038-1101 â€” noting that
   Grad.fs itself is a pre-typecheck rewrite over the *untyped* AST, not an
   analysis), the lattice-summary framing of `computeAllSymcomStates`
   (IR.fs; `SymcomState`, formalism.md:918), riding behind the
   `monomorphizeModule` worklist. Also: close future.md Â§4b.4 (reynolds
   self-licensing) as a definitional case, and revive the `antisymm` pin
   spelling (dead `CnAntisymm`) so deduced-Neg kernels are pinnable.
4. **Confirm-and-pin UX â€” DISPLAY HALF DONE (2026-07-25).** Landed: stage
   3's suggestions reach editor tooling as structured diagnostics â€” `blade
   ide check --json` emits each as severity `warning`, code **BL4010**
   ("confirm-and-pin storage suggestion", registered in the BL4xxx
   constraints family), spanned to the KERNEL (synthesized eta wrappers
   fall back to the former expression's source span, so the ghost
   annotation anchors on `object_for(f)`). Plumbed via the
   `PinSuggestions` AsyncLocal side-channel (the `IdePartial` pattern â€” no
   signature ripple); the plain-string warning twin is deduplicated out of
   the JSON; CLI output unchanged; the message text contains the exact
   pin clause to insert. No new lowering â€” dense-until-pinned is the
   existing default. **Strict half DONE (2026-07-26):** Â§6.1's CI mode
   shipped as `--strict-pins` on `check`/`compile`/`emit`/`run` â€” every
   outstanding suggestion re-emitted as an ERROR-severity BL4010 at the
   kernel span, exit 1, default behavior untouched (see Â§6.1 for the
   gate sites and the deliberate `blade ide check` exclusion). Tested by
   the in-process "Strict Pins" block (`blade test strict-pins`) over
   the functions/026 vs 029 twins: a flag's behavior is not expressible
   as a corpus entry, so the block drives `checkFile` and `compileFile`
   directly. Remaining: extension-side one-click apply-edit (out of
   repo), REPL display of deduced-but-unpinned classes, the
   library-author exchange-law check for call-site-less Poly kernels.

**Shippability property worth preserving:** stages 2â€“3 are *observationally
inert* â€” pure analysis plus diagnostics; storage changes only ever happen via
pinned annotations, which are today's already-tested syntax, so the
differential gates (tests/Differential.fs â€” independent F# oracle;
tests/InterpDiff.fs â€” interpreter vs compiled binary, with the `arity`,
`symmetry`, and `index-types` slices already wired at InterpDiff.fs:74,84,130)
are unaffected until a user pins.

**Test plan:** corpus twins around the existing pairs â€” arity/022 (dense) vs
arity/028 (pinned triangle) â€” plus: suggestion-diagnostic tests on the 022
shape and a fixed-arity twin; declared-comm-vs-deduced-Neg error tests;
functions/003 as the comm-without-identity case (Â§6.3). New deduction tests
slot into the existing category harness (`blade test <category>` /
`blade test interp <category>`).

## 8. New proof obligations

- **Soundness of symmetry deduction:** the composed judgment of Â§3.2.1 implies
  the kernel is genuinely invariant/anti-invariant under the corresponding
  transpositions. Decomposes into: the adjacent-transpositions-generate-Sâ‚™
  lemma (clean, small); the finite per-primitive base cases; and the
  **multi-operand sign-composition lemma** (signs multiply across
  simultaneously-Neg operands â€” the crux, per the `(aâˆ’b)*(aâˆ’b)` case), under
  the stated **closed-world side condition** (no table entry â‡’ âŠ¥). Statements
  are over the abstract semiring (float ulp caveats scoped in Â§6.7). This is
  the missing general form of `deduced_commutativity`.
- **âˆ€-arity exchange-law lemma** (separate, for the strict-mode/library path):
  assoc+comm of the combiner + the step-vs-base check imply all-arity
  adjacent-pair invariance for head::tail-recursive packs; and its negative
  twin â€” no antisymmetric combiner satisfies the signed exchange law
  (Â§3.2.2).
- **Soundness of minimum-rank inference:** the max-join cell rank is the least
  rank at which the body type-checks, and lifting the cell over any frame
  preserves the denotation (rank polymorphism already assumed; state it
  against the deduced cell rank).
- Both compose with the existing exact `H âˆ© Stab` lowering (`license_exactness`,
  BladeCompleteness; formalism.md Â§11.2) at the call site unchanged â€” the
  deductions produce the *inputs* to that law, they do not modify it.
