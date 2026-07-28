# Plan: equivariance deduction — deepening stage 6a

Status: LANDED 2026-07-28. Base: branch `w-integrate` (feat/derive-sym-tp
plus the deduction analysis+surfacing round). All three work items merged;
gates green (full suite + interp differential). Landed-note deviations from
the spec below, all agent-caught and correct:

- The seam, not the inference function, writes `GalCertSuggestions` (the
  spec's both-write instruction would have double-emitted and failed the
  SUGGEST harness); `inferGalileanCertificates` writes `CertFacts` and
  returns the strings — the exact equiv split.
- A function with SEVERAL passing galilean candidates is proposed for each
  but threaded into the speculative table for none (an ambiguous closure
  note would be unactionable; pinned by ml-equiv/084's `total_flux`).
- The optional `joinStatus` Opaque-absorption was NOT taken (soundness
  relaxation on the shared checking path for near-zero recall, and it
  degrades the branch-disagreement diagnostic).
- The walker's new component-read arms swallow a base-judgment Error into
  `Opaque` (the old arms never walked the base, so propagating a new Error
  would have ADDED rejects), and `ExprTupleIndex` also requires the selector
  invariant. The `deduced[]` renderer needed a fix for the new kinds — the
  name field (the group) was silently dropped by the pair-fields branch.
- Corpus-wide noise sweep (all 1196 files): exactly ONE pre-existing file
  newly suggests — sgs/015's Smagorinsky closure, a true positive
  (`ml.galilean(ub)` on `smag_g`).

## What already exists (do not rebuild)

Stage 6a (`MLEquiv.inferCertificates`, MLEquiv.fs:1559) already runs the shipped
equivariance CHECKING judgment speculatively over uncertified functions and
proposes `where ml.equiv(G)` pins as BL4011 warnings on the `CertSuggestions`
side-channel: signature-driven candidates strongest-first (O3 > SO3; single
registered point group), the non-vacuity filter, decl-order speculative
dependency threading with the "(also requires pinning: …)" closure, and the
polynomial-engine rescue inherited for free. Declared clauses are HARD-CHECKED
(BL4008/BL4009/BL4012) — unlike `where comm` before this session's round,
equivariance never had a trusted-not-checked hole, so no BL4013-style
contradiction validator is needed here.

The docs anticipated this round by name (plan-transforms-as-types.md §3.5):
"MLEquiv is already the lattice … Deduction mode is the same judgment run
Deduce.fs-style, proposing a certificate instead of demanding one." The named
deferrals this plan discharges: Galilean inference (MLEquiv.fs:1495-1497), the
stronger-group upgrade lint (§7 named deferrals), and structured surfacing.

## The gaps this round closes

- **E1 — Galilean deduction** (the axis-sized deferral). No `ml.galilean`
  inference exists. Unlike equiv, it needs NO type annotations (GalSig is
  built from the clause + param names only), so recall is not gated on
  fully-annotated signatures.
- **E2 — structured deduced-certificate facts.** BL4011 is string-only; the
  IDE's `deduced[]` array (kinds rank/comm/antisymm/packComm) has no
  equivariance kind, so certificate provenance is invisible as data.
- **E3 — walker recall + wording.** `ExprField`/catch-all produce `Opaque`,
  which poisons joins and — via `List.tryFindIndex (isInv >> not)` — gets
  reported with the WRONG words: an unclassifiable argument is called a
  "representation-typed value" (MLEquiv.fs:1048/1071/1073; MLGalilean.fs:399
  has the same bug with "boost-variant").
- **E4 — stronger-group upgrade lint.** A function pinned `ml.equiv(SO3)`
  that also judges under O3 should be told the stronger certificate is
  available — guarded, because certificates do not transfer between groups,
  so upgrading a helper that certified callers depend on would break them.
- **E5 — `// SUGGEST:` harness must speak Galilean** (generalized in the
  skeleton commit: the block drains CertSuggestions ++ GalCertSuggestions).

Consciously NOT this round: Sₙ/perm inference (flat-extent keying is
ambiguous at a signature — proposing N would be noise; still deferred),
partial-annotation equiv proposals (elaboration runs before typecheck, so
deduced ranks/types don't exist yet at the seam; a post-typecheck second
inference pass is an architecture change — deferred with this note), and any
`--strict-pins` arm for BL4011/BL4014 (certificates own no storage decision).

## Skeleton (coordinator, pre-committed — agents build on it)

1. `MLEquiv.CertFacts` — structured twin of CertSuggestions:
   `type CertFact = { Owner: string; Discipline: string; Group: string;
   Deps: string list }` + AsyncLocal module (reset/add/get), entries paired
   with the decl Span. Discipline is "equiv" | "galilean"; Group is the group
   name for equiv, the comma-joined velocity params for galilean.
2. `MLGalilean.GalCertSuggestions` — string channel, same shape as
   CertSuggestions. BL4014's channel.
3. Diagnostics registry: `BL4014 "galilean certificate suggestion"` (warning,
   always; no strict-pins arm — the BL4011 precedent).
4. `MLElaborate.expandStr` resets both new channels beside CertSuggestions.
5. `Test_DiagCorpus.runCertSuggestTests` drains BOTH string channels — pins
   in ml-equiv corpus files assert the union, silence still asserts silence.

Compile order allows all of it: MLEquiv (114) < MLGalilean (115) <
MLElaborate (122) < Ide (159); MLGalilean may reference MLEquiv.CertFacts.

## G1 — Galilean inference (owns MLGalilean.fs, MLElaborate.fs; corpus 078-084)

`inferGalileanCertificates (mlAliases) (sgsAliases) (gcerts) (decls)
  : (string * Span) list` in MLGalilean.fs, mirroring inferCertificates:

- Consider each `DeclFunction` with no `__ml_galilean` conjunct, not in
  `gcerts`, not self-recursive (freeVars check — no summary proves itself).
- **Candidates**: let F = params that occur free in the body (CertShell
  freeVars — occurrence IS the vacuity guard: a free occurrence of p∈S looks
  up BVar, so an unmentioned param can never be part of an honest
  certificate). Try each singleton {p}, p ∈ F, in param order; propose EVERY
  passing singleton (each is an independent true claim). If none passed and
  |F| ≥ 2, try the full set F once (covers `u - v` velocity-difference
  bodies, where singletons fail but the joint boost passes); propose if it
  passes. No other subsets — the combinatorics are not worth v1.
- Speculative GalSig table threads in decl order (one table — no groups),
  with the deps/order closure note exactly like equiv's
  "(also requires pinning: …)".
- Message: `function '<name>' judges boost-invariant with velocity
  parameter(s) <p, q>: add 'where ml.galilean(<p, q>)'<closure note>`.
- Emit to `GalCertSuggestions` AND `CertFacts`
  (Discipline="galilean", Group = comma-joined params, Deps = closure).
- Hook at the galilean seam in MLElaborate (~1917-1930): restructure so
  inference runs when declared galilean certs judge clean, INCLUDING when
  `gcerts` is empty (compute sgsAliases outside the short-circuit), mirroring
  the equiv seam's comment block.
- Propose ⊆ Check-accept holds by construction: checking `ml.galilean(S)`
  needs only Validate (params exist) + judgeFunction — exactly what inference
  ran. Every SUGGEST test gets a pinned twin proving it.
- While in the file: fix the BOpaque wording at the uncertified-escape site
  (~:399) — an unclassifiable argument must not be called "boost-variant".
  Check ml-equiv corpus ERROR-CONTAINS pins before rewording.

## G2 — equiv walker recall + upgrade lint (owns MLEquiv.fs; corpus 085-092)

- **Walker arms** (judge, ~522-669): `ExprTupleIndex` and `ExprField` on a
  base that judges `Inv` return `Inv InvShapeUnknown` (components of an
  invariant are invariant); any other base status keeps today's `Opaque`.
  Every new accept needs a soundness comment and both-directions corpus
  coverage (the arm is shared with CHECKING — a wrong accept is a false
  certificate).
- **Wording fix** at judgeApp (~1048-1073): split the `isInv >> not` failure
  into "unclassifiable" vs genuinely-Rep wording. Optional, only if
  corpus-neutral: let `joinStatus` absorb Opaque (Opaque ⊔ x = Opaque)
  instead of rejecting mixed joins — probe first, keep out if any existing
  reject flips.
- **Upgrade lint**, inside `inferCertificates` (it already receives certs +
  decls): for each DECLARED cert with Group = SO3, run `tryCandidate O3`
  against the real table with the function's own entry replaced by the O3
  hypothesis. On pass, emit BL4011: `function '<name>' is pinned
  ml.equiv(SO3) but judges under O3: the stronger certificate is available`.
  GUARD: suppress when the function's name occurs free in ANY other
  certified function's body (cross-group calls reject both ways — an upgrade
  would break those callers); a corpus test pins the suppression.
- **CertFacts emission** beside the existing string in inferCertificates
  (Discipline="equiv", Group=gs, Deps=ordered closure). Upgrade-lint
  suggestions are strings only (they propose EDITING a pin, not adding one).

## G3 — surfacing + docs (owns Ide.fs, Lowering.fs, TypeCheck.fs warning-twin
region, Cli.fs surfacing test block, tests/Test_DiagCorpus.fs beyond the
skeleton tweak, docs/)

- Ide.ideCheck: BL4014 loop beside the BL4011 one (both arms); `deduced[]`
  gains CertFacts entries — DKind = Discipline ("equiv"/"galilean"),
  DOwner = Owner, DName = Group, DLeft = deps comma-joined, span = decl span.
  Drained on both Ok and Error arms like everything else.
- Lowering.typeCheckWarningDiagnostics: GalCertSuggestions → mkWarning
  "BL4014" beside the BL4011 mapping; extend the skipPins comment (BL4014,
  like BL4011, grows no strict-pins arm).
- TypeCheck.typeCheck string twins (~10726-10732): append galilean strings.
- Cli `blade test surfacing` block: BL4014 render assertion + a CertFacts
  in-process assertion (equiv kind present for a known-suggesting source).
- Docs: plan-transforms-as-types.md §7 — move Galilean inference and the
  upgrade lint from named deferrals to landed notes (with the honest recall
  caveats); note Sₙ inference stays deferred and why. equivariant-nn.md gets
  a one-line pointer if it names the deferral.

## Execution mechanics (standing constraints)

- Up to three Opus agents (G1, G2, G3), EACH IN ITS OWN WORKTREE branched
  from w-integrate + skeleton. Boundaries are per-file and disjoint;
  corpus numbering is pre-assigned (G1: ml-equiv/078-084, G2: 085-092).
- **Agents may NOT run test batches** (`blade test`, `blade test interp`,
  full corpus sweeps). They may build (`dotnet build Blade.fsproj -c
  Release`) and run single-file probes (`blade check/run <file>`). For any
  suite slice they REQUEST a run from the coordinator, who serializes all
  suite runs (shared %TEMP% interp-diff dir; em-dash caveat → PowerShell;
  C:\msys64\ucrt64\bin on PATH).
- Integration: coordinator merges G1, G2, G3 into w-integrate, applies any
  cross-boundary stitches, runs the full solo gates (blade test + interp),
  then the SUGGEST/strict-pins/surfacing in-process blocks.

## Deferred (recorded here so the round can close honestly)

- Sₙ/perm inference (signature keying ambiguity — unchanged verdict).
- Partial-annotation equiv proposals (needs post-typecheck inference pass).
- Maximal-set Galilean candidates beyond singletons + full-F.
- `Rep`-base `ExprField` as a hard reject rather than Opaque (behavioral
  tightening; needs its own corpus round).
- A structured Deps field in `ide check --json` (currently comma-joined in
  DLeft).
